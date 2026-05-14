using System.Security.Cryptography;
using System.Text;
using WKVRCProxy;
using WKVRCProxy.Shared;
using Xunit;

namespace WKVRCProxy.Tests;

public class MeshClientChallengeTests
{
    // Fixed test vector for cross-checking against the server-side test.
    // nonce="dGVzdG5vbmNl" clientId="test-client-id" trustKey="test-secret-key"
    // Expected: HMAC-SHA256("test-secret-key", "dGVzdG5vbmNl\ntest-client-id")
    [Fact]
    public void ComputeChallengeSignature_MatchesReferenceVector()
    {
        const string nonce = "dGVzdG5vbmNl";
        const string clientId = "test-client-id";
        const string trustKey = "test-secret-key";

        // Compute expected value directly to avoid coupling to MeshClient internals
        byte[] keyBytes = Encoding.UTF8.GetBytes(trustKey);
        byte[] data = Encoding.UTF8.GetBytes(nonce + "\n" + clientId);
        byte[] hash = HMACSHA256.HashData(keyBytes, data);
        string expected = Convert.ToHexStringLower(hash);

        string actual = MeshClient.ComputeChallengeSignature(nonce, clientId, trustKey);

        Assert.Equal(expected, actual);
        Assert.Equal(64, actual.Length); // HMAC-SHA256 = 32 bytes = 64 hex chars
        Assert.Equal(actual, actual.ToLowerInvariant()); // must be lowercase
    }

    [Fact]
    public void ComputeChallengeSignature_DifferentNonceProducesDifferentSignature()
    {
        const string clientId = "client-abc";
        const string trustKey = "shared-secret";

        string sig1 = MeshClient.ComputeChallengeSignature("nonce-aaa", clientId, trustKey);
        string sig2 = MeshClient.ComputeChallengeSignature("nonce-bbb", clientId, trustKey);

        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public void ComputeChallengeSignature_DifferentClientIdProducesDifferentSignature()
    {
        const string nonce = "YWJjMTIz";
        const string trustKey = "shared-secret";

        string sig1 = MeshClient.ComputeChallengeSignature(nonce, "client-x", trustKey);
        string sig2 = MeshClient.ComputeChallengeSignature(nonce, "client-y", trustKey);

        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public void ComputeChallengeSignature_PayloadIsNonceLfClientId()
    {
        // Verify the exact payload format: nonce + "\n" + clientId (not clientId + "\n" + nonce)
        const string nonce = "AAAA";
        const string clientId = "BBBB";
        const string trustKey = "key";

        // Correct order
        byte[] key = Encoding.UTF8.GetBytes(trustKey);
        byte[] correctPayload = Encoding.UTF8.GetBytes("AAAA\nBBBB");
        string expectedSig = Convert.ToHexStringLower(HMACSHA256.HashData(key, correctPayload));

        string actual = MeshClient.ComputeChallengeSignature(nonce, clientId, trustKey);
        Assert.Equal(expectedSig, actual);

        // Reversed order must differ
        byte[] wrongPayload = Encoding.UTF8.GetBytes("BBBB\nAAAA");
        string wrongSig = Convert.ToHexStringLower(HMACSHA256.HashData(key, wrongPayload));
        Assert.NotEqual(wrongSig, actual);
    }

    [Fact]
    public void HelperChallengeResponseFrame_SerializesWithCorrectActionAndSignature()
    {
        // Wire-shape regression: server must receive action="helper_challenge_response"
        // and a non-null "signature" field.
        var frame = new HelperChallengeResponseFrame
        {
            Action = WireConstants.ActionHelperChallengeResponse,
            Signature = "deadbeef",
        };

        string json = System.Text.Json.JsonSerializer.Serialize(frame);
        Assert.Contains("\"action\":\"helper_challenge_response\"", json);
        Assert.Contains("\"signature\":\"deadbeef\"", json);
    }
}
