using Content.Shared._ForkStation.PersistentPrisoner;

namespace Content.Client._ForkStation.PersistentPrisoner;

public sealed class PenaltyConsoleBoundUserInterface : BoundUserInterface
{
    private PenaltyConsoleWindow? _window;

    public PenaltyConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = new PenaltyConsoleWindow();
        _window.OnPlayerSelected += name =>
            SendMessage(new PenaltyConsoleSelectPlayer(name));
        _window.OnAddPenalty += (name, rounds, reason) =>
            SendMessage(new PenaltyConsoleAddPenalty(name, rounds, reason));
        _window.OnUndoPenalty += id =>
            SendMessage(new PenaltyConsoleUndoPenalty(id));
        _window.OnClose += Close;
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is PenaltyConsoleState cast)
            _window?.UpdateState(cast);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _window?.Close();
    }
}
