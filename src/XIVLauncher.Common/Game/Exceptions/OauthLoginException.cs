using System;
using System.Text.RegularExpressions;
using Serilog;

namespace XIVLauncher.Common.Game.Exceptions;

[Serializable]
public class OauthLoginException : Exception
{
    public string? OauthErrorMessage { get; private set; }

    public OauthLoginException(string document)
    {
        this.OauthErrorMessage = document;
    }
}