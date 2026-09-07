using System;
using System.IO;
using System.Text;
using Historia.Services.Profiles;
using Xunit;

namespace Historia.Tests;

public class ProfileExportCryptoTests
{
    [Fact]
    public void Round_trips_the_payload()
    {
        const string json = """{"profiles":[{"name":"work","token":"ghp_secret"}]}""";
        var blob = ProfileExportCrypto.Encrypt(json, "correct horse battery staple");
        Assert.Equal(json, ProfileExportCrypto.Decrypt(blob, "correct horse battery staple"));
    }

    [Fact]
    public void Current_bundles_declare_version_2()
    {
        var blob = ProfileExportCrypto.Encrypt("{}", "pw");
        Assert.Equal("EPOCHEXP2", Encoding.ASCII.GetString(blob, 0, 9));
    }

    [Fact]
    public void Wrong_password_is_rejected_by_the_gcm_tag()
    {
        var blob = ProfileExportCrypto.Encrypt("{}", "right");
        Assert.Throws<InvalidDataException>(() => ProfileExportCrypto.Decrypt(blob, "wrong"));
    }

    [Fact]
    public void A_non_historia_blob_is_rejected()
    {
        Assert.Throws<InvalidDataException>(
            () => ProfileExportCrypto.Decrypt(new byte[128], "pw"));
    }
}
