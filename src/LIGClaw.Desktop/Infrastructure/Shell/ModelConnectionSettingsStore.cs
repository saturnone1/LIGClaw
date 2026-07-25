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
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.SetValue("BaseUrl", settings.BaseUrl, RegistryValueKind.String);
        key.SetValue("Model", settings.Model, RegistryValueKind.String);
        WriteCredential(settings.ApiKey);
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

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
