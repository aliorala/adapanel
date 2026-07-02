namespace AdaPanel.Core.Dtos.Update;

/// <summary>Güncellemelerin çekileceği GitHub deposunu (owner/repo) ayarlama isteği. Boş = GitHub kapalı.</summary>
public sealed class SetUpdateGithubRequest
{
    public string Repo { get; set; } = string.Empty;
}
