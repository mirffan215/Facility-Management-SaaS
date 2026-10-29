using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Fms.BuildingBlocks.Cloud;
using Fms.BuildingBlocks.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fms.UnitTests;

public sealed class FieldEncryptionTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static KeyRing Ring(params (string Id, string Key)[] keys) =>
        new(new FieldEncryptionOptions { Keys = keys.ToDictionary(k => k.Id, k => k.Key), CurrentKeyId = keys.Length == 0 ? null : keys[^1].Id });

    [Fact]
    public void A_value_is_unreadable_when_stored_and_comes_back_exactly()
    {
        var ring = Ring(("k1", NewKey()));
        var stored = ring.Protect("omar.k@example.com", "col")!;
        Assert.StartsWith("enc1:k1:", stored);
        Assert.DoesNotContain("omar", stored);
        Assert.Equal("omar.k@example.com", ring.Unprotect(stored, "col"));
        Assert.Equal("تجربة 🙂", ring.Unprotect(ring.Protect("تجربة 🙂", "col"), "col"));
    }

    [Fact]
    public void The_same_text_gives_a_different_stored_value_each_time()
    {
        var ring = Ring(("k1", NewKey()));
        Assert.NotEqual(ring.Protect("same", "col"), ring.Protect("same", "col"));
    }

    [Fact]
    public void Null_stays_null_and_without_a_key_nothing_changes()
    {
        var ring = Ring(("k1", NewKey()));
        Assert.Null(ring.Protect(null, "col"));
        Assert.Null(ring.Unprotect(null, "col"));
        var none = Ring();
        Assert.False(none.IsEnabled);
        Assert.Equal("plain", none.Protect("plain", "col"));
    }

    [Fact]
    public void Text_written_before_encryption_was_switched_on_is_still_readable()
    {
        Assert.Equal("old@example.com", Ring(("k1", NewKey())).Unprotect("old@example.com", "col"));
    }

    [Fact]
    public void A_value_moved_to_another_column_or_tampered_with_does_not_decrypt()
    {
        var ring = Ring(("k1", NewKey()));
        var stored = ring.Protect("secret", "visits.email")!;
        Assert.ThrowsAny<CryptographicException>(() => ring.Unprotect(stored, "visits.phone"));
        var bytes = Convert.FromBase64String(stored.Split(':', 3)[2]);
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => ring.Unprotect("enc1:k1:" + Convert.ToBase64String(bytes), "visits.email"));
    }

    [Fact]
    public void After_a_rotation_old_values_still_read_and_new_ones_use_the_new_key()
    {
        var oldKey = NewKey();
        var old = Ring(("k1", oldKey)).Protect("kept", "col")!;
        var rotated = new KeyRing(new FieldEncryptionOptions { Keys = new() { ["k1"] = oldKey, ["k2"] = NewKey() }, CurrentKeyId = "k2" });
        Assert.Equal("kept", rotated.Unprotect(old, "col"));
        Assert.StartsWith("enc1:k2:", rotated.Protect("new", "col"));
    }

    [Fact]
    public void A_value_written_with_a_key_that_is_gone_is_reported_not_returned_as_garbage()
    {
        var stored = Ring(("k1", NewKey())).Protect("x", "col");
        var other = Ring(("k9", NewKey()));
        var ex = Assert.Throws<CryptographicException>(() => other.Unprotect(stored, "col"));
        Assert.Contains("k1", ex.Message);
    }

    [Theory]
    [InlineData("not base64!", "k1")]
    [InlineData("AAAA", "k1")] // too short for 32 bytes
    [InlineData("", "k1")]
    public void A_bad_key_stops_start_up(string key, string id) =>
        Assert.Throws<InvalidOperationException>(() => new KeyRing(new FieldEncryptionOptions { Keys = new() { [id] = key } }));

    [Fact]
    public void Several_keys_need_a_named_current_one_and_it_must_exist()
    {
        var keys = new Dictionary<string, string> { ["a"] = NewKey(), ["b"] = NewKey() };
        Assert.Throws<InvalidOperationException>(() => new KeyRing(new FieldEncryptionOptions { Keys = keys }));
        Assert.Throws<InvalidOperationException>(() => new KeyRing(new FieldEncryptionOptions { Keys = keys, CurrentKeyId = "c" }));
        Assert.Throws<InvalidOperationException>(() => new KeyRing(new FieldEncryptionOptions { Keys = new() { ["a:b"] = NewKey() } }));
    }

    [Fact]
    public void Required_without_a_key_refuses_to_start_and_the_settings_are_restored_for_other_tests()
    {
        try
        {
            Assert.Throws<InvalidOperationException>(() => FieldEncryption.Configure(new FieldEncryptionOptions { Required = true }));
            FieldEncryption.Configure(new FieldEncryptionOptions { Keys = new() { ["k1"] = NewKey() } });
            Assert.True(FieldEncryption.IsEnabled);
            var converter = FieldEncryption.Converter("col");
            var to = converter.ConvertToProviderExpression.Compile();
            var from = converter.ConvertFromProviderExpression.Compile();
            Assert.Equal("hello", from(to("hello")));
        }
        finally
        {
            FieldEncryption.Configure(new FieldEncryptionOptions());
        }
    }
}

/// <summary>The scanner against a stand-in for the ClamAV daemon that speaks its INSTREAM protocol.</summary>
public sealed class ClamAvScannerTests
{
    private sealed class FakeClamd : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public byte[] Received { get; private set; } = [];
        public string Command { get; private set; } = "";

        public FakeClamd(string reply)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var command = new byte[10];
                await stream.ReadExactlyAsync(command);
                Command = Encoding.ASCII.GetString(command);
                var all = new MemoryStream();
                var size = new byte[4];
                while (true)
                {
                    await stream.ReadExactlyAsync(size);
                    var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(size);
                    if (length == 0) break;
                    var chunk = new byte[length];
                    await stream.ReadExactlyAsync(chunk);
                    all.Write(chunk);
                }

                Received = all.ToArray();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
            });
        }

        public void Dispose() => _listener.Stop();
    }

    private static ClamAvScanner Scanner(int port, bool failClosed = true) =>
        new(Options.Create(new ScanningOptions { Provider = "ClamAv", Host = "127.0.0.1", Port = port, TimeoutSeconds = 5, FailClosed = failClosed }), NullLogger<ClamAvScanner>.Instance);

    [Fact]
    public async Task A_clean_file_is_streamed_whole_in_chunks_and_accepted()
    {
        using var clamd = new FakeClamd("stream: OK\0");
        var content = RandomNumberGenerator.GetBytes(200_000); // more than one chunk
        var outcome = await Scanner(clamd.Port).ScanAsync(content);
        Assert.True(outcome.IsClean);
        await Task.Delay(100);
        Assert.Equal("zINSTREAM\0", clamd.Command);
        Assert.Equal(content, clamd.Received);
    }

    [Fact]
    public async Task A_threat_is_refused_with_its_name()
    {
        using var clamd = new FakeClamd("stream: Win.Test.EICAR_HDB-1 FOUND\0");
        var outcome = await Scanner(clamd.Port).ScanAsync("x"u8.ToArray());
        Assert.False(outcome.IsClean);
        Assert.Equal("Win.Test.EICAR_HDB-1", outcome.Threat);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task An_unreachable_scanner_refuses_the_file_unless_the_operator_chose_to_let_it_through(bool failClosed, bool clean)
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop(); // nothing listens here now
        Assert.Equal(clean, (await Scanner(port, failClosed).ScanAsync("x"u8.ToArray())).IsClean);
    }

    [Theory]
    [InlineData("INSTREAM size limit exceeded. ERROR\0", false)]
    [InlineData("garbage\0", false)]
    public async Task An_answer_that_is_not_ok_is_not_trusted(string reply, bool clean)
    {
        using var clamd = new FakeClamd(reply);
        Assert.Equal(clean, (await Scanner(clamd.Port).ScanAsync("x"u8.ToArray())).IsClean);
    }
}
