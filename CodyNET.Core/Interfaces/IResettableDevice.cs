namespace CodyNET.Core.Interfaces;

/// <summary>
/// A device whose registers survive a CPU reset (the ROM does not re-initialize all of them) but
/// should go back to their power-on state when the machine is cold-started, so a newly loaded
/// program does not inherit e.g. sprites or sounds from the previous one.
/// </summary>
public interface IResettableDevice
{
    /// <summary>Restores the power-on state of the device's registers.</summary>
    void PowerOnReset();
}
