namespace AdaPanel.Core.Interfaces;

public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}

