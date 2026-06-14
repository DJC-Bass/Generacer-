/// <summary>
/// Shared flag so gameplay scripts can suppress conflicting inputs while a
/// full-screen menu (store / inventory) is open. The menus run in real game
/// time (no pause), and the gamepad's A / B buttons are shared between driving
/// (Jump / Turbo) and the menus (Buy / Close). Driving checks this flag before
/// acting on those buttons.
///
/// The menu scripts run with a late DefaultExecutionOrder so that on the frame a
/// menu closes, CarController has already read input THIS frame while AnyOpen was
/// still true — preventing the closing "B" press from leaking into a turbo.
/// </summary>
public static class MenuState
{
    /// <summary>True while any store / inventory menu is currently shown.</summary>
    public static bool AnyOpen;
}
