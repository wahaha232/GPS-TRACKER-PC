using System.Text;
using GpsTrackerPc.Core.Security;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class TokenStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _tokenFile;

    public TokenStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _tokenFile = Path.Combine(_root, "github-token.bin");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 測試暫存資料夾清不掉不影響結果。
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsThroughDpapi()
    {
        var store = new TokenStore(_tokenFile);
        Assert.False(store.HasToken);

        store.Save("test-token-1234567890");

        Assert.True(store.HasToken);
        Assert.Equal("test-token-1234567890", store.Load());
        Assert.True(store.TryLoad(out var token));
        Assert.Equal("test-token-1234567890", token);
    }

    [Fact]
    public void StoredFile_DoesNotContainPlaintext()
    {
        const string token = "plaintext-token-should-not-appear";
        var store = new TokenStore(_tokenFile);

        store.Save(token);

        var bytes = File.ReadAllBytes(_tokenFile);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(token, text);
    }

    [Fact]
    public void Save_ReplacesExistingToken()
    {
        var store = new TokenStore(_tokenFile);
        store.Save("first-token");
        store.Save("second-token");

        Assert.Equal("second-token", store.Load());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void Delete_RemovesFileAndStatus()
    {
        var store = new TokenStore(_tokenFile);
        store.Save("to-be-deleted");

        store.Delete();

        Assert.False(store.HasToken);
        Assert.Null(store.Load());
        Assert.False(File.Exists(_tokenFile));
    }

    [Fact]
    public void CorruptFile_IsTreatedAsNotReadable()
    {
        File.WriteAllBytes(_tokenFile, new byte[] { 1, 2, 3, 4, 5 });
        var store = new TokenStore(_tokenFile);

        Assert.True(store.HasToken);
        Assert.Null(store.Load());
        Assert.False(store.TryLoad(out var token));
        Assert.Null(token);
    }

    [Fact]
    public void EmptyToken_IsRejected()
    {
        var store = new TokenStore(_tokenFile);

        Assert.Throws<ArgumentException>(() => store.Save(string.Empty));
        Assert.Throws<ArgumentException>(() => store.Save("   "));
        Assert.False(store.HasToken);
    }

    [Fact]
    public void TokenFile_IsWrittenAtomicallyWithoutTempLeftovers()
    {
        var store = new TokenStore(_tokenFile);
        store.Save("token-one");
        store.Save("token-two");

        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }
}
