using System;

namespace XIVLauncher.Common.Game.Exceptions;

[Serializable]
public class OauthLoginException : Exception
{
    public string? OauthErrorMessage { get; private set; }


    public OauthLoginException(string document) : base(document)
    {
        this.OauthErrorMessage = document;
    }
}