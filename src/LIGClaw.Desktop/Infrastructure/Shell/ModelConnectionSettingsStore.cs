using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record ModelConnectionSettings(string BaseUrl, string ApiKey, string Model);

internal sealed class ModelConnectionSettingsStore
{
    private const string SettingsKeyPath = @"Software\LIGClaw\ModelConnection";
    private const string CredentialTarget = "LIGClaw/OpenAICompatibleApiKey";

    public ModelConnectionSettings? Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        var baseUrl = key?.GetValue("BaseUrl") as string;
        var model = key?.GetValue("Model") as string;
        var apiKey = ReadCredential();
        return string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new ModelConnectionSettings(baseUrl, apiKey, model);
    }

    public void Save(ModelConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        var previousBaseUrl = key.GetValue("BaseUrl") as string;
        var previousModel = key.GetValue("Model") as string;
        var previousApiKey = ReadCredential();

        try
        {
            WriteCredential(settings.ApiKey);
            key.SetValue("BaseUrl", settings.BaseUrl, RegistryValueKind.String);
            key.SetValue("Model", settings.Model, RegistryValueKind.String);
        }
        catch (Exception saveException)
        {
            var rollbackFailures = new List<Exception>();
            try { RestoreValue(key, "BaseUrl", previousBaseUrl); }
            catch (Exception exception) { rollbackFailures.Add(exception); }
            try { RestoreValue(key, "Model", previousModel); }
            catch (Exception exception) { rollbackFailures.Add(exception); }
            try { RestoreCredential(previousApiKey); }
            catch (Exception exception) { rollbackFailures.Add(exception); }
            if (rollbackFailures.Count > 0)
                throw new AggregateException("모델 연결 설정 저장과 이전 상태 복원에 실패했습니다.", [saveException, .. rollbackFailures]);
            throw;
        }
    }

    private static void RestoreValue(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void RestoreCredential(string? secret)
    {
        if (secret is not null) WriteCredential(secret);
        else if (!CredDelete(CredentialTarget, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new InvalidOperationException("Windows 자격 증명 관리자의 이전 상태를 복원하지 못했습니다.");
    }

    private static string? ReadCredential()
    {
        if (!CredRead(CredentialTarget, 1, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.CredentialBlob == nint.Zero
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static void WriteCredential(string secret)
    {
        if (secret.Length > 1_280) throw new InvalidOperationException("API 키가 Windows 저장 한도를 초과합니다.");
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new Credential
            {
                Type = 1,
                TargetName = CredentialTarget,
                CredentialBlobSize = (uint)(secret.Length * sizeof(char)),
                CredentialBlob = blob,
                Persist = 2,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException("Windows 자격 증명 관리자에 API 키를 저장하지 못했습니다.");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
