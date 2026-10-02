using StardewValley;

namespace ValleyTalkMemory;

/// <summary>
/// Local mirror of ValleyTalk's public mod-provided API (ValleyTalk.IValleyTalkInterface).
/// SMAPI proxies the call by member name/signature, so we intentionally do NOT reference
/// ValleyTalk.dll at compile time (that would break whenever the mod updates).
/// </summary>
public interface IValleyTalkApi
{
    void SetModName(string modName);

    bool IsEnabledForCharacter(NPC character);

    void RegisterPromptOverride(string characterName, string promptElement, string overrideText);

    void ClearPromptOverride(string characterName, string promptElement);

    void ClearPromptOverrides(string characterName = "");
}
