namespace EntraUserSync.Core.Configuration;

public interface IAppSettingsProvider
{
    EffectiveSettings GetEffective();
    event EventHandler? Changed;
    void Reload();
}
