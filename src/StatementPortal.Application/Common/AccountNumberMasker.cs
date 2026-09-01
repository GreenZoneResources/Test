namespace StatementPortal.Application.Common;

/// <summary>
/// US-11: account numbers must be stored/displayed in masked form in the audit
/// trail. Masking happens here, at write time, so plaintext account numbers
/// never land in the audit store at all — not just hidden in the UI on top of
/// a plaintext record.
/// </summary>
public static class AccountNumberMasker
{
    private const int VisibleTrailingDigits = 4;

    public static string Mask(string accountNumber)
    {
        if (string.IsNullOrWhiteSpace(accountNumber))
            return string.Empty;

        var trimmed = accountNumber.Trim();

        if (trimmed.Length <= VisibleTrailingDigits)
            return new string('*', trimmed.Length);

        var visible = trimmed[^VisibleTrailingDigits..];
        return new string('*', trimmed.Length - VisibleTrailingDigits) + visible;
    }
}
