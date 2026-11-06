using System;

namespace Brom.Models;

public sealed class DeviceInfo
{
    public string DeviceName { get; set; }
    public string DeviceType { get; set; }
    public string Port { get; set; }
    public string VID { get; set; }
    public string PID { get; set; }
    public string DeviceId { get; set; }
    public string SerialNumber { get; set; }

    public string Model { get; set; }
    public string Csc { get; set; }
    public string ApVersion { get; set; }
    public string BlVersion { get; set; }
    public string CpVersion { get; set; }
    public string CscVersion { get; set; }
    public string BinaryBit { get; set; }
    public string Imei { get; set; }
    public string LockStatus { get; set; }
    public string PhoneNumber { get; set; }
    public string Country { get; set; }
    public string UsbMode { get; set; }
    public string UniqueNumber { get; set; }
    public string OsVersion { get; set; }
    public string FrpLock { get; set; }

    public override string ToString()
    {
        string sn = string.IsNullOrEmpty(SerialNumber) ? "" : $" [{SerialNumber}]";
        string port = string.IsNullOrEmpty(Port) || Port == "N/A" ? "" : $" ({Port})";
        return $"{DeviceType}: {DeviceName}{sn}{port}";
    }
}

public sealed class PartitionInfo
{
    public string Name { get; set; }
    public int Lun { get; set; }
    public string LunDisplay { get; set; }
    public long StartLba { get; set; }
    public long SizeSectors { get; set; }
    public int SectorSize { get; set; } = 512;
    public string SizeFormatted { get; set; }
    public string LocalFilePath { get; set; }
    public string FilePath { get; set; }
    public string Group { get; set; }
    public bool IsChecked { get; set; }
    public bool Enable { get; set; } = true;
}
