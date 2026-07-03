namespace TelegArm.Core
{
    /// <summary>
    /// Telegram API credentials (api_id / api_hash), kept OUT of source control.
    ///
    /// This committed file holds only BUILD PLACEHOLDERS. The real values live in
    /// <c>ApiCredentials.Local.cs</c> (gitignored), which supplies them through the
    /// <see cref="FillId"/> / <see cref="FillHash"/> partial-method hooks. When that file
    /// is absent (a fresh public clone) the compiler ELIDES the partial-method calls
    /// (an unimplemented partial method compiles to nothing), the placeholders below
    /// remain, and the project still builds 0/0 — it simply won't authenticate until a
    /// contributor drops in their own credentials.
    ///
    /// To supply yours: register an app at https://my.telegram.org/apps, then copy
    /// <c>ApiCredentials.Local.cs.example</c> → <c>ApiCredentials.Local.cs</c> and fill it in.
    ///
    /// NOTE: a credential shipped inside ANY client is recoverable from the binary with a
    /// decompiler — that is unavoidable for pure-managed code and does not need solving.
    /// This split's only goal is to keep the secret out of the git history, which it fully
    /// achieves (the real values never enter a committed file).
    /// </summary>
    public static partial class ApiCredentials
    {
        /// <summary>Telegram api_id — 0 (placeholder) unless ApiCredentials.Local.cs supplies the real value.</summary>
        public static int ApiId
        {
            get { int v = 0; FillId(ref v); return v; }
        }

        /// <summary>Telegram api_hash — placeholder unless ApiCredentials.Local.cs supplies the real value.</summary>
        public static string ApiHash
        {
            get { string v = "PLACEHOLDER_PUT_YOURS_IN_LOCAL_FILE"; FillHash(ref v); return v; }
        }

        // Implemented ONLY in the gitignored ApiCredentials.Local.cs. If that file is missing, the compiler
        // removes these calls (partial methods with no body compile to nothing), leaving the placeholders above.
        static partial void FillId(ref int value);
        static partial void FillHash(ref string value);
    }
}
