using System;
using System.Threading;
using System.Threading.Tasks;
using Brom.Models;
using Brom.Protocols;
using Brom.Services;

namespace Brom.Protocols;

/// <summary>
/// Static facade for MTP / modem AT operations. The UI calls these helpers so
/// it never has to manage the <see cref="MtpProtocol"/> lifetime itself.
/// </summary>
public static class MtpMode
{
    public static async Task ReadInfoAsync(string selectedDeviceId, Action<string, LogType> logOutput)
    {
        using var mtp = new MtpProtocol(new DelegatedLogSink(logOutput), PathManager.Instance);
        await mtp.ReadInfoAsync(selectedDeviceId, CancellationToken.None);
    }

    /// <summary>Writes a new 3-letter CSC / sales code.</summary>
    public static async Task<bool> ChangeCscAsync(string selectedDeviceId, string newCsc,
        Action<string, LogType> logOutput)
    {
        using var mtp = new MtpProtocol(new DelegatedLogSink(logOutput), PathManager.Instance);
        return await mtp.ChangeCscAsync(selectedDeviceId, newCsc, CancellationToken.None);
    }

    public static async Task<bool> RebootDownloadAsync(string selectedDeviceId, Action<string, LogType> logOutput)
    {
        using var mtp = new MtpProtocol(new DelegatedLogSink(logOutput), PathManager.Instance);
        return await mtp.RebootDownloadAsync(selectedDeviceId, CancellationToken.None);
    }

    public static async Task FormatMtpAsync(Action<string, LogType> logOutput, string selectedDeviceId = "")
    {
        using var mtp = new MtpProtocol(new DelegatedLogSink(logOutput), PathManager.Instance);
        await mtp.FormatMtpAsync(selectedDeviceId, CancellationToken.None);
    }


}
