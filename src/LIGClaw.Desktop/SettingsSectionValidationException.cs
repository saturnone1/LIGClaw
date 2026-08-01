namespace LIGClaw.Desktop;

internal enum ModelProfileField
{
    None,
    BaseUrl,
    Model,
    ApiKey,
}

internal sealed class SettingsSectionValidationException(
    string message,
    ModelProfileField field = ModelProfileField.None) : Exception(message)
{
    public ModelProfileField Field { get; } = field;
}
