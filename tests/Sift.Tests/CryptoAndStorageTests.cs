using System.Text;
using Sift.Core.Crypto;
using Sift.Core.Model;
using Sift.Core.Storage;
using Sift.Tests.Support;

namespace Sift.Tests;

public sealed class ExportCryptoTests
{
    private const string Passphrase = "correct horse battery staple";
    private const int FastIterations = ExportCrypto.MinIterations;
    private static readonly byte[] Plaintext = Encoding.UTF8.GetBytes("""{"merchant":"Northwind Coffee","total":18.76}""");

    private static byte[] Encrypt() => ExportCrypto.Encrypt(Plaintext, Passphrase, FastIterations);

    [Fact]
    public void Round_trips()
    {
        Assert.Equal(Plaintext, ExportCrypto.Decrypt(Encrypt(), Passphrase));
    }

    [Fact]
    public void Same_input_encrypts_differently_each_time()
    {
        Assert.NotEqual(Encrypt(), Encrypt());   // fresh salt and nonce
    }

    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext()
    {
        Assert.DoesNotContain("Northwind", Encoding.UTF8.GetString(Encrypt()));
    }

    // Layout: magic 0-3 | version 4 | kdf 5 | iterations 6-9 | salt 10-25 | nonce 26-37 | tag 38-53 | ciphertext 54+
    [Theory]
    [InlineData(12, "salt")]
    [InlineData(30, "nonce")]
    [InlineData(40, "tag")]
    [InlineData(60, "ciphertext")]
    public void Any_modified_byte_fails_authentication(int offset, string region)
    {
        var data = Encrypt();
        data[offset] ^= 0x01;

        var error = Assert.Throws<SiftCryptoException>(() => ExportCrypto.Decrypt(data, Passphrase));
        Assert.True(error.Message.Contains("modified"), region);
    }

    [Fact]
    public void Modified_iteration_count_fails()
    {
        var data = Encrypt();
        data[9] ^= 0x01;   // still inside the allowed range, but no longer what was authenticated

        Assert.Throws<SiftCryptoException>(() => ExportCrypto.Decrypt(data, Passphrase));
    }

    [Fact]
    public void Wrong_passphrase_fails()
    {
        Assert.Throws<SiftCryptoException>(() => ExportCrypto.Decrypt(Encrypt(), "not the passphrase at all"));
    }

    [Fact]
    public void Unknown_version_is_rejected()
    {
        var data = Encrypt();
        data[4] = 2;

        var error = Assert.Throws<SiftCryptoException>(() => ExportCrypto.Decrypt(data, Passphrase));
        Assert.Contains("version", error.Message);
    }

    [Fact]
    public void Hostile_iteration_count_is_rejected_before_any_work()
    {
        var data = Encrypt();
        data[6] = 0x7F;   // ~2 billion iterations

        var error = Assert.Throws<SiftCryptoException>(() => ExportCrypto.Decrypt(data, Passphrase));
        Assert.Contains("parameters", error.Message);
    }

    [Fact]
    public void Other_files_and_truncated_files_are_rejected()
    {
        Assert.Throws<SiftCryptoException>(() => ExportCrypto.Decrypt("PK\u0003\u0004 not ours"u8.ToArray(), Passphrase));
        Assert.Throws<SiftCryptoException>(() => ExportCrypto.Decrypt(Encrypt()[..40], Passphrase));
    }

    [Fact]
    public void Short_passphrases_are_refused()
    {
        Assert.Throws<ArgumentException>(() => ExportCrypto.Encrypt(Plaintext, "short", FastIterations));
    }

    [Fact]
    public async Task Receipts_round_trip_through_export()
    {
        var receipt = (await Sift.Core.Pipeline.ReceiptPipeline.CreateDefault().ProcessAsync(Fixtures.Load("dev", "02-contoso-electronics-plain").Raw)).Receipt!;

        var restored = ReceiptExporter.Import(ReceiptExporter.Export(receipt, Passphrase, FastIterations), Passphrase);

        Assert.Equal(receipt.Total, restored.Total);
        Assert.Equal(receipt.Items, restored.Items);
        Assert.Equal(receipt.Adjustments, restored.Adjustments);
        Assert.Equal(receipt.ImportId, restored.ImportId);
    }

    [Fact]
    public void Export_file_names_reveal_nothing_about_the_receipt()
    {
        Assert.Equal("sift-receipt-20261015-093000.sift",
            ReceiptExporter.FileName(new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Dpapi_protects_and_unprotects_for_the_current_user()
    {
        if (!OperatingSystem.IsWindows()) return;

        var protector = new DpapiKeyProtector();
        var key = new byte[32];
        Random.Shared.NextBytes(key);

        var wrapped = protector.Protect(key);

        Assert.NotEqual(key, wrapped);
        Assert.Equal(key, protector.Unprotect(wrapped));
    }
}

public sealed class LocalReceiptStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sift-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private LocalReceiptStore Open(byte secret = 0x5A) => new(_dir, new FakeKeyProtector(secret));

    private static Receipt Sample(string name = "01-northwind-coffee-html") =>
        Sift.Core.Pipeline.ReceiptPipeline.CreateDefault().ProcessAsync(Fixtures.Load("dev", name).Raw).GetAwaiter().GetResult().Receipt!;

    [Fact]
    public void Saves_lists_loads_and_deletes()
    {
        using var store = Open();
        var receipt = Sample();

        var id = store.Save(receipt);

        var summary = Assert.Single(store.List());
        Assert.Equal("Northwind Coffee", summary.Merchant);
        Assert.Equal(18.76m, summary.Total);
        Assert.Equal(receipt.Total, store.Load(id)!.Total);

        Assert.True(store.Delete(id));
        Assert.Empty(store.List());
        Assert.Null(store.Load(id));
    }

    [Fact]
    public void Nothing_is_stored_in_plaintext()
    {
        using (var store = Open()) store.Save(Sample());

        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            var content = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("Northwind", content);
            Assert.DoesNotContain("18.76", content);
        }
    }

    [Fact]
    public void Reopening_with_the_same_protector_reads_existing_receipts()
    {
        string id;
        using (var store = Open()) id = store.Save(Sample());

        using var reopened = Open();
        Assert.NotNull(reopened.Load(id));
    }

    [Fact]
    public void A_different_user_cannot_unlock_the_store()
    {
        using (var store = Open()) store.Save(Sample());

        Assert.Throws<SiftCryptoException>(() => Open(secret: 0x33));
    }

    [Fact]
    public void Finds_duplicates_by_message_id_and_by_content()
    {
        using var store = Open();
        var receipt = Sample();
        var id = store.Save(receipt);

        Assert.Equal(id, store.FindDuplicate(receipt.ImportId));
        Assert.Equal(id, store.FindDuplicate(receipt.ImportId with { MessageIdHash = null }));
        Assert.Null(store.FindDuplicate(new ImportId("other", "other")));
    }

    [Fact]
    public void Ids_that_are_not_guids_are_refused()
    {
        using var store = Open();

        Assert.Null(store.Load("..\\..\\secrets"));
        Assert.False(store.Delete("../index"));
    }

    [Fact]
    public void Tampered_receipt_file_fails_its_integrity_check()
    {
        using var store = Open();
        var id = store.Save(Sample());
        var path = Path.Combine(_dir, "receipts", id + ".sift");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<SiftCryptoException>(() => store.Load(id));
    }

    [Fact]
    public void Swapping_receipt_files_is_detected()
    {
        using var store = Open();
        var first = store.Save(Sample("01-northwind-coffee-html"));
        var second = store.Save(Sample("02-contoso-electronics-plain"));
        File.Copy(Path.Combine(_dir, "receipts", second + ".sift"), Path.Combine(_dir, "receipts", first + ".sift"), overwrite: true);

        Assert.Throws<SiftCryptoException>(() => store.Load(first));
    }
}
