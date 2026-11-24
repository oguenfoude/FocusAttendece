namespace FocusPlus.Hardware;

/// <summary>Commands of the ZKTeco standalone communication protocol (port 4370).</summary>
public static class ZkCommand
{
    public const ushort DbRrq = 7;
    public const ushort UserWrq = 8;
    public const ushort UserTempRrq = 9;
    public const ushort UserTempWrq = 10;
    public const ushort OptionsRrq = 11;
    public const ushort OptionsWrq = 12;
    public const ushort AttLogRrq = 13;
    public const ushort ClearData = 14;
    public const ushort ClearAttLog = 15;
    public const ushort DeleteUser = 18;
    public const ushort DeleteUserTemp = 19;
    public const ushort ClearAdmin = 20;
    public const ushort GetFreeSizes = 50;
    public const ushort GetTime = 201;
    public const ushort SetTime = 202;
    public const ushort RegEvent = 500;
    public const ushort Connect = 1000;
    public const ushort Exit = 1001;
    public const ushort EnableDevice = 1002;
    public const ushort DisableDevice = 1003;
    public const ushort Restart = 1004;
    public const ushort PowerOff = 1005;
    public const ushort Sleep = 1006;
    public const ushort RefreshData = 1013;
    public const ushort RefreshOption = 1014;
    public const ushort TestVoice = 1017;
    public const ushort GetVersion = 1100;
    public const ushort Auth = 1102;
    public const ushort PrepareData = 1500;
    public const ushort Data = 1501;
    public const ushort FreeData = 1502;
    public const ushort PrepareBuffer = 1503;
    public const ushort ReadBuffer = 1504;

    public const ushort AckOk = 2000;
    public const ushort AckError = 2001;
    public const ushort AckData = 2002;
    public const ushort AckRetry = 2003;
    public const ushort AckRepeat = 2004;
    public const ushort AckUnauth = 2005;
    public const ushort AckUnknown = 0xFFFF;
    public const ushort AckErrorCommand = 0xFFFD;
    public const ushort AckErrorInit = 0xFFFC;
    public const ushort AckErrorData = 0xFFFB;

    /// <summary>Function code for user records.</summary>
    public const int FctUser = 5;
}

/// <summary>Raised when a terminal answers with an unexpected frame or a protocol error.</summary>
public sealed class ZkException(string message, ushort? code = null) : Exception(message)
{
    public ushort? Code { get; } = code;
}

/// <summary>Connection parameters for one terminal.</summary>
public sealed record ZkEndpoint(string IpAddress, int Port = 4370, int CommKey = 0)
{
    public override string ToString() => $"{IpAddress}:{Port}";
}
