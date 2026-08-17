using System.Security.Cryptography;
using System.Text;
using Linebot_jam.Options;
using Microsoft.Extensions.Options;

namespace Linebot_jam.Services;

public class LineSignatureValidator : ILineSignatureValidator
{
    private readonly string _channelSecret;

    public LineSignatureValidator(IOptions<LineOptions> options)
    {
        _channelSecret = options.Value.ChannelSecret;
    }

    public bool IsValidSignature(byte[] requestBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(_channelSecret))
            return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_channelSecret));
        var computedHash = hmac.ComputeHash(requestBody);
        var computedSignature = Convert.ToBase64String(computedHash);

        var computedBytes = Encoding.UTF8.GetBytes(computedSignature);
        var headerBytes = Encoding.UTF8.GetBytes(signatureHeader);

        return computedBytes.Length == headerBytes.Length
            && CryptographicOperations.FixedTimeEquals(computedBytes, headerBytes);
    }
}
