using System.Runtime.InteropServices;

namespace Srm.Runtime.Native;

internal static class WfpNative
{
    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmEngineOpen0(
        string? serverName,
        uint authnService,
        IntPtr authIdentity,
        ref FWPM_SESSION0 session,
        out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmProviderAdd0(
        IntPtr engineHandle,
        ref FWPM_PROVIDER0 provider,
        IntPtr sd);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmSubLayerAdd0(
        IntPtr engineHandle,
        ref FWPM_SUBLAYER0 subLayer,
        IntPtr sd);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmFilterAdd0(
        IntPtr engineHandle,
        ref FWPM_FILTER0 filter,
        IntPtr sd,
        out ulong id);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmProviderDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    internal static extern void FwpmFreeMemory0(ref IntPtr p);

    // srm stop（RunCommandとは別プロセス起動）が、後からフィルターを列挙して削除するために使う。
    // enumTemplateにIntPtr.Zeroを渡すとシステム全体のフィルターを列挙するので、
    // 呼び出し側でsubLayerKeyが一致するものだけを選別する。
    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmFilterCreateEnumHandle0(
        IntPtr engineHandle,
        IntPtr enumTemplate,
        out IntPtr enumHandle);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmFilterEnum0(
        IntPtr engineHandle,
        IntPtr enumHandle,
        uint numEntriesRequested,
        out IntPtr entries,
        out uint numEntriesReturned);

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    internal static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);

    // AppPath識別子（tier2-guest-network-egress）: 実行ファイルのフルパスから
    // FWPM_CONDITION_ALE_APP_ID条件に使うappId（FWP_BYTE_BLOB）を導出する。
    // 返されたポインタはFwpmFreeMemory0で解放する必要がある。
    [DllImport("fwpuclnt.dll", SetLastError = false, CharSet = CharSet.Unicode)]
    internal static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    internal const uint RPC_C_AUTHN_WINNT = 10;
    internal const uint ERROR_SUCCESS = 0;
    // FWP_ACTION_FLAG_TERMINATING(0x1000) | アクションID。PERMITはID=2、BLOCKはID=1。
    internal const uint FWP_ACTION_BLOCK = 0x00001001;
    internal const uint FWP_ACTION_PERMIT = 0x00001002;
    internal const uint FWPM_LAYER_ALE_AUTH_CONNECT_V4_GUID = 0;

    internal static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 =
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    internal static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V6 =
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    internal static readonly Guid FWPM_CONDITION_ALE_PACKAGE_ID =
        new("71bc78fa-f17c-4997-a602-6abb261f351c");
    // tier2-guest-network-egress: AppContainerを経由しないTier2アプリ向けの識別子条件。
    // 値はWindows SDK 10.0.26100.0のfwpmu.hから転記（推測・記憶に頼らず実機のヘッダーで確認済み）。
    internal static readonly Guid FWPM_CONDITION_ALE_APP_ID =
        new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    internal static readonly Guid FWPM_CONDITION_ALE_USER_ID =
        new("af043a0a-b34d-4f86-979c-c90371af6e66");
    internal static readonly Guid FWPM_CONDITION_IP_REMOTE_ADDRESS =
        new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FWPM_SESSION0
    {
        public Guid sessionKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public uint txnWaitTimeoutInMSec;
        public int processId;
        public IntPtr sid;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? username;
        [MarshalAs(UnmanagedType.Bool)]
        public bool kernelMode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FWPM_DISPLAY_DATA0
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? name;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FWPM_PROVIDER0
    {
        public Guid providerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public FWP_BYTE_BLOB providerData;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? serviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FWPM_SUBLAYER0
    {
        public Guid subLayerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public ushort weight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_BYTE_BLOB
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FWPM_FILTER0
    {
        public Guid filterKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public Guid layerKey;
        public Guid subLayerKey;
        public FWP_VALUE0 weight;
        public uint numFilterConditions;
        public IntPtr filterCondition;
        public FWPM_ACTION0 action;
        public ulong rawContext;
        public Guid reservedGuid;
        public ulong filterId;
        public FWP_VALUE0 effectiveWeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_FILTER_CONDITION0
    {
        public Guid fieldKey;
        public FWP_MATCH_TYPE matchType;
        public FWP_CONDITION_VALUE0 conditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_CONDITION_VALUE0
    {
        public FWP_DATA_TYPE type;
        public FWP_VALUE_UNION value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_VALUE0
    {
        public FWP_DATA_TYPE type;
        public FWP_VALUE_UNION value;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FWP_VALUE_UNION
    {
        [FieldOffset(0)] public uint uint32;
        [FieldOffset(0)] public IntPtr sd;
        [FieldOffset(0)] public IntPtr byteArray16;
        // AppPath識別子（FWP_BYTE_BLOB_TYPE）用。FWP_VALUE0_の共用体では
        // FWP_BYTE_BLOB *byteBlob がsid/sdと同じオフセット0にある
        // （Windows SDK fwptypes.hのFWP_VALUE0_定義で確認済み）。
        [FieldOffset(0)] public IntPtr byteBlob;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_ACTION0
    {
        public uint type;
        public Guid filterType;
    }

    internal enum FWP_DATA_TYPE : uint
    {
        FWP_EMPTY = 0,
        FWP_UINT8 = 1,
        FWP_UINT16 = 2,
        FWP_UINT32 = 3,
        FWP_UINT64 = 4,
        FWP_INT8 = 5,
        FWP_INT16 = 6,
        FWP_INT32 = 7,
        FWP_INT64 = 8,
        FWP_FLOAT = 9,
        FWP_DOUBLE = 10,
        FWP_BYTE_ARRAY16_TYPE = 11,
        FWP_BYTE_BLOB_TYPE = 12,
        FWP_SID = 13,
        FWP_SECURITY_DESCRIPTOR_TYPE = 14,
        FWP_TOKEN_INFORMATION_TYPE = 15,
        FWP_TOKEN_ACCESS_INFORMATION_TYPE = 16,
        FWP_UNICODE_STRING_TYPE = 17,
        FWP_BYTE_ARRAY6_TYPE = 18,
    }

    internal enum FWP_MATCH_TYPE : uint
    {
        FWP_MATCH_EQUAL = 0,
        FWP_MATCH_GREATER = 1,
        FWP_MATCH_LESS = 2,
        FWP_MATCH_GREATER_OR_EQUAL = 3,
        FWP_MATCH_LESS_OR_EQUAL = 4,
        FWP_MATCH_RANGE = 5,
        FWP_MATCH_FLAGS_ALL_SET = 6,
        FWP_MATCH_FLAGS_ANY_SET = 7,
        FWP_MATCH_FLAGS_NONE_SET = 8,
        FWP_MATCH_EQUAL_CASE_INSENSITIVE = 9,
        FWP_MATCH_NOT_EQUAL = 10,
        FWP_MATCH_TYPE_MAX = 11,
    }
}
