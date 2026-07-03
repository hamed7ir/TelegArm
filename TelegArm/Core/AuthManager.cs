using System;
using System.Threading;

namespace TelegArm.Core
{
    /// <summary>
    /// Drives the interactive login state machine. The WTelegramClient config
    /// callback runs on a background thread and blocks on WaitForCode/WaitForPassword
    /// until the UI supplies the value via SubmitCode/SubmitPassword.
    /// </summary>
    public static class AuthManager
    {
        private static string _code;
        private static string _password;

        private static readonly AutoResetEvent _codeReady = new AutoResetEvent(false);
        private static readonly AutoResetEvent _passwordReady = new AutoResetEvent(false);

        /// <summary>Phone number (international format, e.g. +15551234567) set by the UI before login.</summary>
        public static string PhoneNumber { get; set; }

        /// <summary>Raised (on the background login thread) when Telegram asks for the verification code.</summary>
        public static event Action CodeRequested;

        /// <summary>Raised (on the background login thread) when Telegram asks for the 2FA password.</summary>
        public static event Action PasswordRequested;

        /// <summary>Blocks the login thread until the UI submits the verification code.</summary>
        public static string WaitForCode()
        {
            CodeRequested?.Invoke();
            _codeReady.WaitOne();
            return _code;
        }

        /// <summary>Called by the UI to unblock WaitForCode with the user-entered code.</summary>
        public static void SubmitCode(string code)
        {
            _code = code;
            _codeReady.Set();
        }

        /// <summary>Blocks the login thread until the UI submits the 2FA password.</summary>
        public static string WaitForPassword()
        {
            PasswordRequested?.Invoke();
            _passwordReady.WaitOne();
            return _password;
        }

        /// <summary>Called by the UI to unblock WaitForPassword with the user-entered password.</summary>
        public static void SubmitPassword(string password)
        {
            _password = password;
            _passwordReady.Set();
        }

        /// <summary>Resets transient state so a fresh login attempt can run.</summary>
        public static void Reset()
        {
            PhoneNumber = null;
            _code = null;
            _password = null;
            _codeReady.Reset();
            _passwordReady.Reset();
        }
    }
}
