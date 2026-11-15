namespace TushonkaHorse
{
    // Read by name by the F12 settings window (ConfigurationManager). Only the field the mod sets is declared; the
    // window ignores the ones that are missing.
#pragma warning disable 0649
    internal sealed class ConfigurationManagerAttributes
    {
        // Hidden until "Advanced settings" is ticked at the top of the window.
        public bool? IsAdvanced;
    }
#pragma warning restore 0649
}
