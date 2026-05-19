using Volo.Abp.Settings;

namespace Dignite.Paperbase.Settings;

public class PaperbaseSettingDefinitionProvider : SettingDefinitionProvider
{
    public override void Define(ISettingDefinitionContext context)
    {
        context.Add(
            new SettingDefinition(
                PaperbaseSettings.OcrConfidenceThreshold,
                defaultValue: "0.85",
                isVisibleToClients: false,
                isInherited: true,
                isEncrypted: false));
    }
}
