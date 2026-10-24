namespace RalFsrHelper.Core;

/// <summary>
/// ROG Ally Life's R2 bucket, through its r2.dev address. The FSR 4.1.2 driver files come from here
/// (fsr4/amdxcffx64.dll and fsr4/amdvkffx64.dll); OptiScaler comes from its own GitHub nightly release.
/// The media.rogallylife.com custom domain isn't used: it sits behind the zone's bot protection, which refuses the
/// helper with 403.
/// </summary>
public static class R2Files
{
    public const string Base = "https://pub-b170628a8d834de98909a5878680e8d8.r2.dev/";

    public const string Fsr4Folder = Base + "fsr4/";
}
