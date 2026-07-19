using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Srm.Runtime.Native;

namespace Srm.Runtime;

public class RestrictedAccount
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required IntPtr Sid { get; init; }
    // AclManager.GrantAccess/GrantExecuteAccessの共有Traverse付与先として渡す
    // SRM-RestrictedApps共有ローカルグループのSID（design.mdの二段構え、
    // AppContainer向けのAllAppPackagesSidに相当するもの）。
    public required IntPtr SharedGroupSid { get; init; }
}

// restricted-account-app-isolation: design.mdの二段構え（祖先Traverse権限は全ポリシー共有の
// ローカルグループへ、allow_pathsへの実アクセス権はポリシーごとの専用アカウントへ）のうち、
// アカウント・グループ自体の払い出しを担う。AppContainerSidFactory（DC-010、決定的な
// SID導出）と同じ思想で、アカウント名もポリシー名から決定的に導出し、既存アカウントが
// あれば再利用する（毎回の作成コストを避ける）。
public class RestrictedAccountManager
{
    // SAMアカウント名は20文字制限があるため、ポリシー名をそのまま使わずMD5ハッシュの
    // 先頭12桁だけを使う（"srm-ra-" 7文字 + 12文字 = 19文字）。
    public const string SharedGroupName = "SRM-RestrictedApps";

    public static string DeriveAccountName(string policyName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(policyName));
        var hex = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return $"srm-ra-{hex}";
    }

    // 既存アカウントがあればパスワードだけ新規ランダム値にリセットして再利用し
    // （design.md: アカウントIDは永続、パスワードは平文で永続化しない使い捨て）、
    // 無ければ作成する。戻り値のPasswordはこの呼び出し元プロセス内でLogonUserに
    // 使うためだけの一時値であり、呼び出し元は使用後速やかに破棄すること。
    public static RestrictedAccount EnsureAccount(string policyName)
    {
        var username = DeriveAccountName(policyName);
        var password = GenerateRandomPassword();

        if (AccountExists(username))
            ResetPassword(username, password);
        else
            CreateAccount(username, password);

        EnsureSharedGroup();
        EnsureGroupMembership(username);

        var sid = ResolveSid(username);
        var groupSid = ResolveSid(SharedGroupName);
        return new RestrictedAccount { Username = username, Password = password, Sid = sid, SharedGroupSid = groupSid };
    }

    // design.mdのライフサイクル決定：srm stopではアカウントを削除しない
    // （AppContainerプロファイルも同様に永続させる既存方針と揃える）。本メソッドは
    // 将来のポリシー削除コマンド・テストの後片付け用に用意する。
    public static void DeleteAccount(string policyName)
    {
        var username = DeriveAccountName(policyName);
        var err = AccountNative.NetUserDel(null, username);
        if (err != AccountNative.NERR_Success && err != AccountNative.NERR_UserNotFound)
            throw new InvalidOperationException($"ローカルアカウントの削除に失敗しました: {username} (エラーコード: {err})");
    }

    private static bool AccountExists(string username)
    {
        var err = AccountNative.NetUserGetInfo(null, username, 0, out var bufPtr);
        if (err == AccountNative.NERR_Success)
        {
            AccountNative.NetApiBufferFree(bufPtr);
            return true;
        }
        if (err == AccountNative.NERR_UserNotFound)
            return false;

        throw new InvalidOperationException($"ローカルアカウントの存在確認に失敗しました: {username} (エラーコード: {err})");
    }

    private static void CreateAccount(string username, string password)
    {
        var info = BuildUserInfo(username, password);
        var err = AccountNative.NetUserAdd(null, 1, ref info, out var paramErr);
        // NERR_UserExists: 存在確認とここまでの間に別プロセスが同じポリシー名で
        // 同時に作成した場合のベストエフォート許容（AppContainerLauncherの
        // FWP_E_ALREADY_EXISTS握りつぶしパターンと同じ考え方）。
        if (err != AccountNative.NERR_Success && err != AccountNative.NERR_UserExists)
            throw new InvalidOperationException(
                $"ローカルアカウントの作成に失敗しました: {username} (エラーコード: {err}, パラメータエラー位置: {paramErr})");
    }

    private static void ResetPassword(string username, string password)
    {
        var info = new AccountNative.USER_INFO_1003 { usri1003_password = password };
        var err = AccountNative.NetUserSetInfo(null, username, 1003, ref info, out var paramErr);
        if (err != AccountNative.NERR_Success)
            throw new InvalidOperationException(
                $"ローカルアカウントのパスワードリセットに失敗しました: {username} (エラーコード: {err}, パラメータエラー位置: {paramErr})");
    }

    private static AccountNative.USER_INFO_1 BuildUserInfo(string username, string password) => new()
    {
        usri1_name = username,
        usri1_password = password,
        usri1_password_age = 0,
        usri1_priv = AccountNative.USER_PRIV_USER,
        usri1_home_dir = null,
        usri1_comment = "SRM restricted-account-app-isolation (auto-managed, do not use interactively)",
        usri1_flags = AccountNative.UF_SCRIPT | AccountNative.UF_NORMAL_ACCOUNT |
                      AccountNative.UF_DONT_EXPIRE_PASSWD | AccountNative.UF_PASSWORD_CANT_CHANGE,
        usri1_script_path = null,
    };

    private static void EnsureSharedGroup()
    {
        var group = new AccountNative.LOCALGROUP_INFO_1
        {
            lgrpi1_name = SharedGroupName,
            lgrpi1_comment = "SRM restricted-account-app-isolation: shared ancestor-traverse grant target",
        };
        var err = AccountNative.NetLocalGroupAdd(null, 1, ref group, out var paramErr);
        if (err != AccountNative.NERR_Success && err != AccountNative.ERROR_ALIAS_EXISTS)
            throw new InvalidOperationException(
                $"共有ローカルグループの作成に失敗しました: {SharedGroupName} (エラーコード: {err}, パラメータエラー位置: {paramErr})");
    }

    private static void EnsureGroupMembership(string username)
    {
        var member = new AccountNative.LOCALGROUP_MEMBERS_INFO_3
        {
            lgrmi3_domainandname = $"{Environment.MachineName}\\{username}",
        };
        var err = AccountNative.NetLocalGroupAddMembers(null, SharedGroupName, 3, ref member, 1);
        if (err != AccountNative.NERR_Success && err != AccountNative.ERROR_MEMBER_IN_ALIAS)
            throw new InvalidOperationException(
                $"共有ローカルグループへのメンバー追加に失敗しました: {username} → {SharedGroupName} (エラーコード: {err})");
    }

    private static IntPtr ResolveSid(string username)
    {
        var sidLen = 0;
        var domainLen = 0;
        // 1回目はバッファサイズ0で意図的に失敗させ、必要なsidLen/domainLenを取得する
        // （LookupAccountNameWの標準的な2段階呼び出しパターン）。
        AccountNative.LookupAccountNameW(null, username, IntPtr.Zero, ref sidLen, new StringBuilder(0), ref domainLen, out _);

        var sidPtr = Marshal.AllocHGlobal(sidLen);
        var domainSb = new StringBuilder(domainLen);
        try
        {
            if (!AccountNative.LookupAccountNameW(null, username, sidPtr, ref sidLen, domainSb, ref domainLen, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"アカウントSIDの解決に失敗しました: {username}");
        }
        catch
        {
            Marshal.FreeHGlobal(sidPtr);
            throw;
        }
        return sidPtr;
    }

    private static string GenerateRandomPassword()
    {
        // 対話ログオンを想定しないサービス的アカウントのため、複雑さ要件
        // （大文字・小文字・数字・記号の混在、十分な長さ）を満たす使い捨てパスワードを
        // 生成するだけでよい。生成のたびに新しいパスワードになる（design.mdの
        // 「パスワードは平文で永続化しない」方針）。
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*";
        var bytes = RandomNumberGenerator.GetBytes(32);
        var sb = new StringBuilder(32);
        foreach (var b in bytes)
            sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }
}
