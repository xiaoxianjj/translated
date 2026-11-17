namespace Translated
{
    // Only the settings for this request. Later saves cannot reroute an already queued job.
    internal sealed class TranslationSettings
    {
        internal readonly bool Configured, Traditional;
        internal readonly Provider Provider;
        TranslationSettings(Preferences preferences)
        {
            Configured = preferences.Configured;
            Traditional = preferences.Traditional;
            var profile = preferences.Profile(preferences.Active);
            Provider = new Provider { Name=profile.Name, BaseUrl=profile.BaseUrl,
                Model=profile.Model, Reasoning=profile.Reasoning };
        }
        internal static TranslationSettings Capture(Preferences preferences) => new TranslationSettings(preferences);
    }
}
