namespace AdaPanel.Core.Dtos.Sites;

public sealed class ComposerCommandRequest
{
    // Çalıştırılacak composer alt komutu (ör. "update", "require monolog/monolog", "dump-autoload").
    public string Command { get; set; } = string.Empty;
}
