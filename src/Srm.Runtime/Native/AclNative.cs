using System.Runtime.InteropServices;

namespace Srm.Runtime.Native;

internal static class AclNative
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint SetNamedSecurityInfo(
        string pObjectName,
        SE_OBJECT_TYPE ObjectType,
        SECURITY_INFORMATION SecurityInfo,
        IntPtr psidOwner,
        IntPtr psidGroup,
        IntPtr pDacl,
        IntPtr pSacl);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetNamedSecurityInfo(
        string pObjectName,
        SE_OBJECT_TYPE ObjectType,
        SECURITY_INFORMATION SecurityInfo,
        out IntPtr ppsidOwner,
        out IntPtr ppsidGroup,
        out IntPtr ppDacl,
        out IntPtr ppSacl,
        out IntPtr ppSecurityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern uint SetEntriesInAcl(
        uint cCountOfExplicitEntries,
        [In] EXPLICIT_ACCESS[] pListOfExplicitEntries,
        IntPtr OldAcl,
        out IntPtr NewAcl);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertStringSidToSidW(string StringSid, out IntPtr Sid);

    // tier2-guest-network-egress: FWPM_CONDITION_ALE_USER_ID用の自己相対セキュリティ記述子を
    // 構築するために使う。実機検証で、FWPM_CONDITION_ALE_USER_IDはFWPM_CONDITION_ALE_PACKAGE_ID
    // と違い生のSID（FWP_SID型）を条件値として受け付けず、FWP_E_TYPE_MISMATCH(0x80320027)で
    // 失敗することを確認済み。Microsoft公式サンプル（Permitting and Blocking Applications and
    // Users）の通り、対象SIDにFWP_ACTRL_MATCH_FILTERアクセスを許可するセキュリティ記述子
    // （FWP_SECURITY_DESCRIPTOR_TYPE）を渡す必要がある。
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint BuildSecurityDescriptorW(
        IntPtr pOwner,
        IntPtr pGroup,
        uint cCountOfAccessEntries,
        [In] EXPLICIT_ACCESS[] pListOfAccessEntries,
        uint cCountOfAuditEntries,
        IntPtr pListOfAuditEntries,
        IntPtr pOldSD,
        out uint pSizeNewSD,
        out IntPtr pNewSD);

    // WFP公式ヘッダー(fwptypes.h)定義: セキュリティ記述子条件がフィルターとの照合可否だけを
    // チェックする際に使うアクセス権ビット。実際の通信許可/拒否とは無関係（DACLがこのビットを
    // 許可しているかどうかだけをFWPが評価する）。
    internal const uint FWP_ACTRL_MATCH_FILTER = 0x00000001;

    // restricted-account-app-isolation: filesystem.allow_pathsフォルダの必須整合性レベル
    // （SACL上のマンダトリラベルACE）をLowへ設定するために使う（`icacls <path>
    // /setintegritylevel (OI)(CI)Low`と同じ操作）。ラベル未設定のオブジェクトは既定で
    // Medium整合性レベル扱いとなりLow整合性レベルのプロセスからの書き込みを拒否するため、
    // allow_pathsには明示的にLowラベル＋SYSTEM_MANDATORY_LABEL_NO_WRITE_UPを設定する必要がある。
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeAcl(IntPtr pAcl, uint nAclLength, uint dwAclRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddMandatoryAce(
        IntPtr pAcl, uint dwAceRevision, uint AceFlags, uint MandatoryPolicy, IntPtr pLabelSid);

    internal const uint ACL_REVISION = 2;
    internal const uint SYSTEM_MANDATORY_LABEL_NO_WRITE_UP = 0x1;

    internal enum SE_OBJECT_TYPE
    {
        SE_FILE_OBJECT = 1,
    }

    [Flags]
    internal enum SECURITY_INFORMATION : uint
    {
        DACL_SECURITY_INFORMATION = 0x00000004,
        LABEL_SECURITY_INFORMATION = 0x00000010,
    }

    internal const uint ERROR_SUCCESS = 0;
    internal const uint ERROR_ACCESS_DENIED = 5;
    internal const uint GENERIC_ALL = 0x10000000;
    internal const uint GENERIC_EXECUTE = 0x20000000;
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint FILE_TRAVERSE = 0x00000020;
    internal const uint FILE_READ_ATTRIBUTES = 0x00000080;
    internal const uint SYNCHRONIZE = 0x00100000;
    internal const uint GRANT_ACCESS = 1;
    internal const uint NO_INHERITANCE = 0x0;
    internal const uint SUB_CONTAINERS_AND_OBJECTS_INHERIT = 0x3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct EXPLICIT_ACCESS
    {
        public uint grfAccessPermissions;
        public uint grfAccessMode;
        public uint grfInheritance;
        public TRUSTEE Trustee;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct TRUSTEE
    {
        public IntPtr pMultipleTrustee;
        public uint MultipleTrusteeOperation;
        public TRUSTEE_FORM TrusteeForm;
        public TRUSTEE_TYPE TrusteeType;
        public IntPtr ptstrName;
    }

    internal enum TRUSTEE_FORM
    {
        TRUSTEE_IS_SID = 0,
        TRUSTEE_IS_NAME = 1,
    }

    internal enum TRUSTEE_TYPE
    {
        TRUSTEE_IS_UNKNOWN = 0,
        TRUSTEE_IS_USER = 1,
        TRUSTEE_IS_GROUP = 2,
    }
}
