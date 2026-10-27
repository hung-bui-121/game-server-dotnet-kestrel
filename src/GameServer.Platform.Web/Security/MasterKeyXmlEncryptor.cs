using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Security;

/// <summary>
/// Encrypts each Data Protection key at rest with AES-256-GCM under the deployment master key (OPS-04, research R7).
/// The stored element names the master key id, so keys written before a master-key rotation stay readable.
/// </summary>
public sealed class MasterKeyXmlEncryptor(string masterKeyId, byte[] masterKey) : IXmlEncryptor
{
    internal const string ElementName = "encryptedKey";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(masterKey, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(masterKeyId));
        }

        CryptographicOperations.ZeroMemory(plaintext);
        var element = new XElement(
            ElementName,
            new XAttribute("masterKeyId", masterKeyId),
            new XAttribute("nonce", Convert.ToBase64String(nonce)),
            new XAttribute("tag", Convert.ToBase64String(tag)),
            Convert.ToBase64String(ciphertext));
        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }

    /// <summary>Binds the ciphertext to the master key id so an element cannot be replayed under another id.</summary>
    internal static byte[] AssociatedData(string masterKeyId) => Encoding.UTF8.GetBytes("gameserver-dataprotection:" + masterKeyId);
}

/// <summary>Created by Data Protection from the type name stored in the key ring (needs the service provider ctor).</summary>
public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var settings = services.GetRequiredService<DataProtectionSettings>();
        var masterKeyId = (string?)encryptedElement.Attribute("masterKeyId")
            ?? throw new CryptographicException("Encrypted key has no masterKeyId.");
        var masterKey = masterKeyId == settings.MasterKeyId
            ? settings.MasterKeyBytes()
            : settings.PreviousMasterKeys.TryGetValue(masterKeyId, out var previous) ? DataProtectionSettings.Decode(previous) : null;
        if (masterKey is not { Length: 32 })
        {
            throw new CryptographicException($"Master key '{masterKeyId}' is not configured; the key ring cannot be decrypted.");
        }

        var nonce = Convert.FromBase64String((string?)encryptedElement.Attribute("nonce") ?? string.Empty);
        var tag = Convert.FromBase64String((string?)encryptedElement.Attribute("tag") ?? string.Empty);
        var ciphertext = Convert.FromBase64String(encryptedElement.Value);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using (var aes = new AesGcm(masterKey, tag.Length))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, MasterKeyXmlEncryptor.AssociatedData(masterKeyId));
            }

            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
