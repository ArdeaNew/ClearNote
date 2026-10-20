namespace Folio.Core;

/// <summary>One local author identity shared by this library's notes.</summary>
public sealed class UserProfile
{
    public string DisplayName { get; set; } = "You";
    public NoteImage? Avatar { get; set; }
}
