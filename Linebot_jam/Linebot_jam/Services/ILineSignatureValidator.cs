namespace Linebot_jam.Services;

public interface ILineSignatureValidator
{
    bool IsValidSignature(byte[] requestBody, string? signatureHeader);
}
