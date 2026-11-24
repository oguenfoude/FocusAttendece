using FocusPlus.Domain.Entities;

namespace FocusPlus.Application;

/// <summary>Current operator and UI language (Arabic RTL by default for OPGI M'Sila).</summary>
public sealed class AppSession
{
    public AppUser? User { get; private set; }

    public string Actor => User?.Username ?? "system";

    public bool IsArabic { get; set; } = true;

    public bool IsSignedIn => User is not null;

    public void SignIn(AppUser user)
    {
        User = user;
        MustChangePassword = user.MustChangePassword;
    }

    public bool MustChangePassword { get; private set; }

    public void SignOut() => User = null;
}
