using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Srm.PolicyEngine.Models;
using Srm.Runtime.Native;

namespace Srm.Runtime;

public class AclManager
{
    public void GrantAccess(List<AllowedPath> allowPaths, IntPtr sidAppContainer) =>
        GrantAccess(allowPaths, sidAppContainer, AllAppPackagesNativeSid.Value, AllAppPackagesSid);

    // restricted-account-app-isolation: 祖先ディレクトリのTraverse権限を書き込む先
    // （sharedTraverseNativeSid/sharedTraverseSid）を呼び出し元が指定できる一般化版。
    // AppContainer向け（既定オーバーロード）はALL APPLICATION PACKAGES（S-1-15-2-1）を
    // 使うのに対し、制限付き専用アカウント向けはSRM-RestrictedApps共有ローカルグループの
    // SIDを渡す想定（design.mdの「祖先Traverseは共有グループへ、allow_pathsへの実アクセスは
    // ポリシーごとの専用アカウントへ」という二段構えのうち前者を担う）。
    public void GrantAccess(List<AllowedPath> allowPaths, IntPtr sidGrantee, IntPtr sharedTraverseNativeSid, SecurityIdentifier sharedTraverseSid)
    {
        foreach (var entry in allowPaths)
        {
            if (!Directory.Exists(entry.Path))
            {
                try { Directory.CreateDirectory(entry.Path); }
                catch { /* ベストエフォート */ }
            }

            GrantTraverseChain(entry.Path, sidGrantee, sharedTraverseNativeSid, sharedTraverseSid);

            uint accessMask = entry.Access.Equals("rw", StringComparison.OrdinalIgnoreCase)
                ? AclNative.GENERIC_ALL
                : AclNative.GENERIC_READ;

            GrantPathAccess(entry.Path, accessMask, sidGrantee, AclNative.SUB_CONTAINERS_AND_OBJECTS_INHERIT);
        }
    }

    // S-1-15-2-1 = APPLICATION PACKAGE AUTHORITY\ALL APPLICATION PACKAGES。
    // AppContainerトークンには常にこのSIDが含まれるため、対象ディレクトリの既存DACLに
    // これが既に十分な権限で含まれていれば書き込み不要と判定できる。
    private static readonly SecurityIdentifier AllAppPackagesSid = new("S-1-15-2-1");

    // GrantTraverseChainで実際に書き込む先はAllAppPackagesSid（個別のAppContainer SIDではなく
    // 全AppContainer共通のSID）にする。祖先ディレクトリのTraverse権限（そのフォルダを
    // 「通過」できるだけで一覧表示や中身の読み取りはできない、最小権限）は、ポリシー名ごとに
    // 個別に持つ意味が薄い一方、個別SIDだと新しいポリシー名を使うたびに毎回ミスして
    // NTFS継承伝播の遅延（最大3分程度）を踏む。全AppContainerに共通の広いSIDへ一度だけ
    // 付与しておけば、以降どのポリシー名で実行してもHasTraverseAccessがヒットしスキップされる。
    private static readonly Lazy<IntPtr> AllAppPackagesNativeSid = new(() =>
    {
        if (!AclNative.ConvertStringSidToSidW("S-1-15-2-1", out var sid) || sid == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ALL APPLICATION PACKAGES SIDの変換に失敗しました");
        return sid;
    });

    // AppContainerトークンは BUILTIN\Users や Authenticated Users を継承しないため、
    // ドライブルート(C:\)や %USERPROFILE% など、既定では ALL APPLICATION PACKAGES に
    // 何の権限も付与されていない祖先ディレクトリで経路解決(traverse)が拒否され、
    // 対象ファイル自身のACLが正しくても ERROR_FILE_NOT_FOUND(2) でCreateProcessが失敗する。
    // 各祖先ディレクトリに対して「このフォルダーのみ」のTraverse権限を付与することで、
    // ディレクトリの一覧表示権限は与えずに経路の通過だけを許可する。
    //
    // ただし C:\Windows\System32 のように既定でALL APPLICATION PACKAGESに
    // 読み取り+実行が付与済みの巨大ディレクトリにSetNamedSecurityInfoで書き込むと、
    // NTFSの自動継承伝播により配下の全ファイルへ波及してしまい、数分〜それ以上
    // ハングしたように見える。既に十分な権限があるディレクトリへの書き込みは
    // 事前にスキップする。
    private static void GrantTraverseChain(string targetPath, IntPtr sidGrantee, IntPtr sharedTraverseNativeSid, SecurityIdentifier sharedTraverseSid)
    {
        string? dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(targetPath)); }
        catch { return; }

        // Traverseに加えてRead Attributes+Synchronizeも毎回まとめて確認・付与する。
        // 一覧表示(List Directory)は与えない最小権限のまま。
        //
        // 経緯: プロジェクトルート検出などで祖先ディレクトリをC:\まで辿るツール
        // （Claude Code CLI で実機確認）は、各階層で「このフォルダ自身の属性を
        // 読めるか」を確認しており、ここがACCESS_DENIEDだと単に失敗を返すのではなく
        // 想定外のコードパスに入ってハングした。ドライブルート(C:\)だけでなく、
        // 途中の祖先(例: C:\Users)でも同じ確認が行われるため、チェーン全体に付与する。
        // SYNCHRONIZEも必須: 同期I/O（FILE_FLAG_SYNCHRONOUS_IO系）でのCreateFileは
        // FILE_READ_ATTRIBUTESだけでなくSYNCHRONIZEも同時に要求するため、これが
        // ACEに含まれていないと要求全体がACCESS_DENIEDになる（実機で確認済み）。
        const uint readAttrMask = AclNative.FILE_READ_ATTRIBUTES | AclNative.SYNCHRONIZE;
        const FileSystemRights readAttrRights = FileSystemRights.ReadAttributes | FileSystemRights.Synchronize;

        while (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            if (!HasDirectoryAccess(dir, sidGrantee, FileSystemRights.Traverse, sharedTraverseSid))
            {
                try
                {
                    GrantPathAccess(dir, AclNative.FILE_TRAVERSE, sharedTraverseNativeSid, AclNative.NO_INHERITANCE);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == AclNative.ERROR_ACCESS_DENIED)
                {
                    /* TrustedInstaller所有などDACL変更不可なディレクトリはベストエフォートで無視 */
                }
            }

            if (!HasDirectoryAccess(dir, sidGrantee, readAttrRights, sharedTraverseSid))
            {
                try
                {
                    GrantPathAccess(dir, readAttrMask, sharedTraverseNativeSid, AclNative.NO_INHERITANCE);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == AclNative.ERROR_ACCESS_DENIED)
                {
                }
            }

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
    }

    private static bool HasDirectoryAccess(string dir, IntPtr sidGrantee, FileSystemRights required, SecurityIdentifier sharedTraverseSid)
    {
        try
        {
            var ourSid = new SecurityIdentifier(sidGrantee);
            var security = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (!sid.Equals(sharedTraverseSid) && !sid.Equals(ourSid)) continue;
                if ((rule.FileSystemRights & required) == required) return true;
            }

            return false;
        }
        catch
        {
            // 判定できない場合は安全側（書き込みを試みる）に倒す
            return false;
        }
    }

    // AppContainerはACLで明示的に許可されたファイルしか実行できないため、
    // 起動対象の実行ファイルにも読み取り+実行権限を付与する。
    // ただしTrustedInstaller所有のシステム保護ファイルはAdministratorでもDACLを
    // 変更できない（ERROR_ACCESS_DENIED）。これらは既定でALL APPLICATION PACKAGES
    // に読み取り+実行が付与済みのため、変更できなくても致命的ではない。
    public void GrantExecuteAccess(string path, IntPtr sidAppContainer) =>
        GrantExecuteAccess(path, sidAppContainer, AllAppPackagesNativeSid.Value, AllAppPackagesSid);

    // restricted-account-app-isolation: GrantAccessと同じ一般化（祖先Traverse書き込み先を
    // 呼び出し元指定可能にする）。制限付き専用アカウントは既定でBUILTIN\Users等を
    // 持たないため、System32配下の共通exeであってもこのメソッドでの明示的な実行権付与が
    // 必須になる（AppContainerと違い「既定で足りているのでスキップ」がほぼ発生しない）。
    public void GrantExecuteAccess(string path, IntPtr sidGrantee, IntPtr sharedTraverseNativeSid, SecurityIdentifier sharedTraverseSid)
    {
        GrantTraverseChain(path, sidGrantee, sharedTraverseNativeSid, sharedTraverseSid);

        try
        {
            GrantPathAccess(path, AclNative.GENERIC_READ | AclNative.GENERIC_EXECUTE, sidGrantee, AclNative.NO_INHERITANCE);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == AclNative.ERROR_ACCESS_DENIED)
        {
        }

        // .NET の自己完結アプリのように、実行対象.exeの隣に.dll/.deps.json等の
        // 依存ファイルが必要な構成だと、.exe自体に実行権があっても隣接ファイルが
        // 読めずに起動が失敗する（apphostが依存アセンブリを解決できないため）。
        // C:\Windows\System32等の既定でALL APPLICATION PACKAGESに読み取り+実行が
        // 付与済みの場所では既に十分なのでスキップし、そうでない場所（自作アプリの
        // 出力フォルダ等）だけディレクトリ全体に読み取り+実行を付与する。
        // GrantTraverseChainと同じ理由で、書き込み先はsharedTraverseNativeSid
        // （個別のグランティSIDではなく共有SID）にする。同じフォルダを別のポリシー名
        // （＝別SID）で使い回しても、2回目以降はこの再帰的な付与（NTFS継承伝播で
        // ファイル数に応じて時間がかかる）を毎回スキップできる。
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                !HasDirectoryAccess(dir, sidGrantee, FileSystemRights.ReadAndExecute, sharedTraverseSid))
            {
                GrantPathAccess(dir, AclNative.GENERIC_READ | AclNative.GENERIC_EXECUTE, sharedTraverseNativeSid, AclNative.SUB_CONTAINERS_AND_OBJECTS_INHERIT);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == AclNative.ERROR_ACCESS_DENIED)
        {
        }
    }

    // restricted-account-app-isolation: Low Integrity Levelプロセスがfilesystem.allow_paths
    // フォルダへ書き込めるようにするため、フォルダのSACLにマンダトリラベルACE（Low +
    // SYSTEM_MANDATORY_LABEL_NO_WRITE_UP）を設定する。`icacls <path> /setintegritylevel
    // (OI)(CI)Low`と同じ操作。ラベル未設定のオブジェクトは既定でMedium整合性レベル扱いと
    // なり、Low整合性レベルのプロセスからの書き込みをOSのMIC機構が拒否するため、
    // allow_pathsには明示的にこのラベルを付与する必要がある（design.md参照）。
    public void SetLowIntegrityLabel(string path)
    {
        if (!AclNative.ConvertStringSidToSidW(AccountNative.LOW_INTEGRITY_SID, out var labelSid) || labelSid == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Low整合性レベルSIDの変換に失敗しました");

        // ACLヘッダー(8バイト)+ACEヘッダー(12バイト)+Low IL SIDの実長に対し、
        // 十分な余裕を持たせた固定長バッファを確保する（S-1-16-4096のSID自体は
        // わずか12バイトだが、将来別の整合性レベルSIDを使う可能性も見込む）。
        const int aclBufferSize = 256;
        var aclPtr = IntPtr.Zero;
        try
        {
            aclPtr = Marshal.AllocHGlobal(aclBufferSize);

            if (!AclNative.InitializeAcl(aclPtr, aclBufferSize, AclNative.ACL_REVISION))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SACLの初期化に失敗しました: {path}");

            if (!AclNative.AddMandatoryAce(
                    aclPtr, AclNative.ACL_REVISION, 0,
                    AclNative.SYSTEM_MANDATORY_LABEL_NO_WRITE_UP, labelSid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"マンダトリラベルACEの追加に失敗しました: {path}");

            uint ret = AclNative.SetNamedSecurityInfo(
                path,
                AclNative.SE_OBJECT_TYPE.SE_FILE_OBJECT,
                AclNative.SECURITY_INFORMATION.LABEL_SECURITY_INFORMATION,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                aclPtr);

            if (ret != AclNative.ERROR_SUCCESS)
                throw new Win32Exception((int)ret, $"Low整合性ラベルの設定に失敗しました: {path}");
        }
        finally
        {
            if (aclPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(aclPtr);
            AclNative.LocalFree(labelSid);
        }
    }

    private static void GrantPathAccess(string path, uint accessMask, IntPtr sidAppContainer, uint inheritance)
    {
        var explicitAccess = new AclNative.EXPLICIT_ACCESS
        {
            grfAccessPermissions = accessMask,
            grfAccessMode = AclNative.GRANT_ACCESS,
            grfInheritance = inheritance,
            Trustee = new AclNative.TRUSTEE
            {
                pMultipleTrustee = IntPtr.Zero,
                MultipleTrusteeOperation = 0,
                TrusteeForm = AclNative.TRUSTEE_FORM.TRUSTEE_IS_SID,
                TrusteeType = AclNative.TRUSTEE_TYPE.TRUSTEE_IS_GROUP,
                ptstrName = sidAppContainer,
            }
        };

        uint ret = AclNative.GetNamedSecurityInfo(
            path,
            AclNative.SE_OBJECT_TYPE.SE_FILE_OBJECT,
            AclNative.SECURITY_INFORMATION.DACL_SECURITY_INFORMATION,
            out _,
            out _,
            out IntPtr oldDacl,
            out _,
            out IntPtr psd);

        if (ret != AclNative.ERROR_SUCCESS)
            throw new Win32Exception((int)ret, $"既存DACLの取得に失敗しました: {path}");

        try
        {
            ret = AclNative.SetEntriesInAcl(1, new[] { explicitAccess }, oldDacl, out IntPtr newDacl);
            if (ret != AclNative.ERROR_SUCCESS)
                throw new Win32Exception((int)ret, $"新しいDACLの生成に失敗しました: {path}");

            try
            {
                ret = AclNative.SetNamedSecurityInfo(
                    path,
                    AclNative.SE_OBJECT_TYPE.SE_FILE_OBJECT,
                    AclNative.SECURITY_INFORMATION.DACL_SECURITY_INFORMATION,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    newDacl,
                    IntPtr.Zero);

                if (ret != AclNative.ERROR_SUCCESS)
                    throw new Win32Exception((int)ret, $"DACLの設定に失敗しました: {path}");
            }
            finally
            {
                AclNative.LocalFree(newDacl);
            }
        }
        finally
        {
            if (psd != IntPtr.Zero) AclNative.LocalFree(psd);
        }
    }
}
