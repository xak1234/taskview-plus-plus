namespace StayView.Core;

// An activation may resume after an animation or a cross-process restore. It only
// belongs to the user until another interaction, desktop switch or request replaces it.
public readonly record struct FocusRequest(int Revision, long Interaction, Guid Desktop)
{
    public bool IsCurrent(int revision, long interaction, Guid desktop, bool active)
        => active && Revision == revision && Interaction == interaction
            && (Desktop == Guid.Empty || Desktop == desktop);
}
