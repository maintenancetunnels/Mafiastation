using System.Diagnostics;
using System.Net;

namespace Mafiastation.AiPilotLab;

public sealed record ClientLaunchSpec(string Name, string Pipe, string Username);

public sealed record ClientLauncherOptions(
    string ClientPath,
    string ServerAddress,
    string OutputDirectory,
    string? DotnetPath = null,
    TimeSpan? StartupTimeout = null,
    IReadOnlyList<string>? ExtraCvars = null);

public sealed class ClientLauncher
{
    public async Task<LaunchedClientGroup> LaunchAsync(
        IReadOnlyList<ClientLaunchSpec> specs,
        ClientLauncherOptions options,
        CancellationToken cancellationToken = default)
    {
        if (specs.Count is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(specs), "Launch count must be from 1 through 32.");
        var clientPath = Path.GetFullPath(options.ClientPath);
        if (!File.Exists(clientPath))
            throw new FileNotFoundException("SS14 client executable or DLL was not found.", clientPath);
        if (string.IsNullOrWhiteSpace(options.ServerAddress) || options.ServerAddress.Length > 256)
            throw new ArgumentException("Server address is required and must be at most 256 characters.", nameof(options));
        if (!IsLoopbackServerAddress(options.ServerAddress))
            throw new ArgumentException("AI pilot clients may connect only to a loopback server address.", nameof(options));
        Directory.CreateDirectory(Path.GetFullPath(options.OutputDirectory));

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 64 || spec.Name.Any(char.IsControl) || !names.Add(spec.Name))
                throw new ArgumentException("Client names must be unique and at most 64 non-control characters.", nameof(specs));
            if (string.IsNullOrWhiteSpace(spec.Username) || spec.Username.Length > 32 || spec.Username.Any(char.IsControl) || !usernames.Add(spec.Username))
                throw new ArgumentException("Client usernames must be unique and at most 32 non-control characters.", nameof(specs));
            if (!pipes.Add(spec.Pipe))
                throw new ArgumentException("Client pipe names must be unique.", nameof(specs));
        }

        var launched = new List<LaunchedClient>();
        try
        {
            foreach (var spec in specs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = new PilotPipeClient(spec.Pipe, TimeSpan.FromSeconds(1));
                launched.Add(StartOne(spec, options, clientPath));
            }

            var group = new LaunchedClientGroup(launched);
            await WaitForBridgesAsync(group, options.StartupTimeout ?? TimeSpan.FromSeconds(60), cancellationToken);
            return group;
        }
        catch
        {
            foreach (var client in launched)
                await client.DisposeAsync();
            throw;
        }
    }

    private static LaunchedClient StartOne(ClientLaunchSpec spec, ClientLauncherOptions options, string clientPath)
    {
        var isDll = Path.GetExtension(clientPath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var executable = isDll ? ResolveDotnetHost(options.DotnetPath) : clientPath;
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(clientPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (isDll)
            startInfo.ArgumentList.Add(clientPath);
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--ai-pilot-local-trusted-bridge");
        startInfo.ArgumentList.Add("--connect");
        startInfo.ArgumentList.Add("--connect-address");
        startInfo.ArgumentList.Add(options.ServerAddress);
        startInfo.ArgumentList.Add("--username");
        startInfo.ArgumentList.Add(spec.Username);
        foreach (var cvar in options.ExtraCvars ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(cvar) || cvar.Length > 512 || cvar.Any(char.IsControl) || !cvar.Contains('='))
                throw new ArgumentException("Extra client CVars must be key=value strings of at most 512 non-control characters.");
            AddCvar(startInfo, cvar);
        }
        // Required bridge settings are last so an accidental duplicate --client-cvar cannot override them.
        // Robust's standalone headless path otherwise reaches an OpenGL-only RSI atlas preload.
        AddCvar(startInfo, "res.texturepreloadingenabled=false");
        AddCvar(startInfo, "mafia.ai_pilot.client_enabled=true");
        AddCvar(startInfo, $"mafia.ai_pilot.pipe_name={spec.Pipe}");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start SS14 client process.");
        var safeName = string.Concat(spec.Name.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        var stdoutPath = Path.Combine(outputDirectory, $"{safeName}.stdout.log");
        var stderrPath = Path.Combine(outputDirectory, $"{safeName}.stderr.log");
        return new LaunchedClient(spec, process, stdoutPath, stderrPath);
    }

    private static void AddCvar(ProcessStartInfo startInfo, string cvar)
    {
        startInfo.ArgumentList.Add("--cvar");
        startInfo.ArgumentList.Add(cvar);
    }

    private static string ResolveDotnetHost(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(configured);
            if (!File.Exists(path))
                throw new FileNotFoundException("Configured dotnet host was not found.", path);
            return path;
        }
        if (Environment.ProcessPath is { } processPath &&
            Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "dotnet",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(local))
            return local;
        throw new FileNotFoundException("Could not locate a dotnet host. Supply --dotnet when launching a client DLL.");
    }

    public static bool IsLoopbackServerAddress(string address)
    {
        var value = address.Contains("://", StringComparison.Ordinal) ? address : $"tcp://{address}";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));
    }

    private static async Task WaitForBridgesAsync(
        LaunchedClientGroup group,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var pending = group.Clients.ToDictionary(client => client.Spec.Name, StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0 && !deadline.IsCancellationRequested)
        {
            foreach (var (name, client) in pending.ToArray())
            {
                if (client.Process.HasExited)
                    throw new InvalidOperationException($"Client '{name}' exited with code {client.Process.ExitCode} before its pilot bridge became ready.");
                try
                {
                    var transport = new PilotPipeClient(client.Spec.Pipe, TimeSpan.FromMilliseconds(500));
                    var exchange = await transport.SendAsync(PilotRequest.Create("status"), deadline.Token);
                    if (exchange.Response.Ok)
                        pending.Remove(name);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
                {
                    // The client may still be loading. The outer deadline remains authoritative.
                }
            }
            if (pending.Count > 0 && !deadline.IsCancellationRequested)
                await Task.Delay(250, deadline.Token);
        }
        if (pending.Count > 0)
            throw new TimeoutException($"Pilot bridge startup timed out for: {string.Join(", ", pending.Keys)}.");
    }
}

public sealed class LaunchedClient : IAsyncDisposable
{
    private readonly FileStream _stdout;
    private readonly FileStream _stderr;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private bool _disposed;

    public ClientLaunchSpec Spec { get; }
    public Process Process { get; }

    internal LaunchedClient(ClientLaunchSpec spec, Process process, string stdoutPath, string stderrPath)
    {
        Spec = spec;
        Process = process;
        _stdout = new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.Read, 16_384, FileOptions.Asynchronous);
        _stderr = new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.Read, 16_384, FileOptions.Asynchronous);
        _stdoutPump = process.StandardOutput.BaseStream.CopyToAsync(_stdout);
        _stderrPump = process.StandardError.BaseStream.CopyToAsync(_stderr);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            // Process already exited or did not finish during cleanup.
        }
        try
        {
            await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            // Best-effort log drainage during process cleanup.
        }
        await _stdout.DisposeAsync();
        await _stderr.DisposeAsync();
        Process.Dispose();
    }
}

public sealed class LaunchedClientGroup : IAsyncDisposable
{
    public IReadOnlyList<LaunchedClient> Clients { get; }

    internal LaunchedClientGroup(IReadOnlyList<LaunchedClient> clients)
    {
        Clients = clients;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in Clients.Reverse())
            await client.DisposeAsync();
    }
}
