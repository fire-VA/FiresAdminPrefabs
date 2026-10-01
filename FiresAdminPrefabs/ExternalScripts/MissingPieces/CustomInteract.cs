using UnityEngine;

// Ported from BentoG/MissingPieces 2.3.0 so its bundle's prefabs resolve this
// script without the mod (and without Jotunn). It fronts a piece that is both a
// container and a sign: plain use opens the container, alt-use edits the sign.
public class CustomInteract : MonoBehaviour, Hoverable, Interactable
{
    public string Name = "";

    public string DefaultInteract = "";

    public string AlternateInteract = "";

    public Container Container;

    public Sign Sign;

    public void Awake()
    {
        if (Container == null) Container = GetComponent<Container>();
        if (Sign == null) Sign = GetComponent<Sign>();
    }

    public bool Interact(Humanoid user, bool hold, bool alt)
    {
        if (alt) return Sign != null && Sign.Interact(user, hold, alt);
        return Container != null && Container.Interact(user, hold, alt);
    }

    public string GetHoverText()
    {
        if (Container == null) return "";

        string hoverText = Container.GetHoverText();
        if (Container.m_checkGuardStone && !PrivateArea.CheckAccess(transform.position, 0f, flash: false))
            return hoverText;

        string altKey = (ZInput.IsNonClassicFunctionality() && ZInput.IsGamepadActive())
            ? "$KEY_AltKeys"
            : "$KEY_AltPlace";
        return hoverText + Localization.instance.Localize(
            "\n[<color=yellow><b>" + altKey + " + $KEY_Use</b></color>] " + AlternateInteract);
    }

    public string GetHoverName()
    {
        return Localization.instance.Localize(Name);
    }

    public bool UseItem(Humanoid user, ItemDrop.ItemData item)
    {
        return false;
    }

    public float GetHoverOffset()
    {
        return 0f;
    }
}
