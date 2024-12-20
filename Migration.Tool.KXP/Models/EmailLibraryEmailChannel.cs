using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Microsoft.EntityFrameworkCore;

namespace Migration.Tool.KXP.Models;

[Table("EmailLibrary_EmailChannel")]
[Index("EmailChannelChannelId", Name = "IX_EmailLibrary_EmailChannel_EmailChannelChannelID")]
public class EmailLibraryEmailChannel
{
    [Key]
    [Column("EmailChannelID")]
    public int EmailChannelId { get; set; }

    [Column("EmailChannelGUID")]
    public Guid EmailChannelGuid { get; set; }

    [Column("EmailChannelChannelID")]
    public int EmailChannelChannelId { get; set; }

    [StringLength(252)]
    public string EmailChannelSendingDomain { get; set; } = null!;

    [StringLength(400)]
    public string EmailChannelServiceDomain { get; set; } = null!;

    [Column("EmailChannelPrimaryContentLanguageID")]
    public int EmailChannelPrimaryContentLanguageId { get; set; }

    [ForeignKey("EmailChannelChannelId")]
    [InverseProperty("EmailLibraryEmailChannels")]
    public virtual CmsChannel EmailChannelChannel { get; set; } = null!;

    [InverseProperty("EmailChannelSenderEmailChannel")]
    public virtual ICollection<EmailLibraryEmailChannelSender> EmailLibraryEmailChannelSenders { get; set; } = new List<EmailLibraryEmailChannelSender>();

    [InverseProperty("EmailConfigurationEmailChannel")]
    public virtual ICollection<EmailLibraryEmailConfiguration> EmailLibraryEmailConfigurations { get; set; } = new List<EmailLibraryEmailConfiguration>();
}
