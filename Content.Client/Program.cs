using System.Linq;
using Robust.Client;
using Robust.Client.Mafiastation.AiPilot;

namespace Content.Client
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Preload the fixed trusted IPC helper before Robust begins resolving sandboxed
            // content modules. It remains inert unless the immutable process arguments prove this
            // is an explicit headless loopback pilot client.
            AiPilotPipeBridge.Preload();

            if (args.Contains(AiPilotPipeBridge.ProcessFlag, StringComparer.Ordinal))
            {
                if (!AiPilotPipeBridge.IsProcessAuthorized)
                {
                    throw new InvalidOperationException(
                        "The local AI pilot bridge requires headless mode, a loopback server, " +
                        "and explicit client/pipe CVars.");
                }

                args = args
                    .Where(argument => !string.Equals(
                        argument,
                        AiPilotPipeBridge.ProcessFlag,
                        StringComparison.Ordinal))
                    .ToArray();
            }

            ContentStart.Start(args);
        }
    }
}
