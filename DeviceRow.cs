using System.Collections.ObjectModel;

namespace WuqueProbe;

public sealed class DeviceRow
{
    public ulong BluetoothAddressValue { get; init; }
    public string Name { get; init; } = "(no name)";
    public string Address => BluetoothAddressValue.ToString("X12");
    public short Rssi { get; init; }
    public string ServiceUuidsText { get; init; } = "";
    public string ManufacturerDataText { get; init; } = "";
    public string RawAdvertisementHex { get; init; } = "";
}
