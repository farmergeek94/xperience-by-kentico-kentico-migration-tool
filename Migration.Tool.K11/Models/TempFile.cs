using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Migration.Tool.K11.Models;

[Table("Temp_File")]
public class TempFile
{
    [Key]
    [Column("FileID")]
    public int FileId { get; set; }

    [Column("FileParentGUID")]
    public Guid FileParentGuid { get; set; }

    public int FileNumber { get; set; }

    [StringLength(50)]
    public string FileExtension { get; set; } = null!;

    public long FileSize { get; set; }

    [StringLength(100)]
    public string FileMimeType { get; set; } = null!;

    public int? FileImageWidth { get; set; }

    public int? FileImageHeight { get; set; }

    public byte[]? FileBinary { get; set; }

    [Column("FileGUID")]
    public Guid FileGuid { get; set; }

    public DateTime FileLastModified { get; set; }

    [StringLength(200)]
    public string FileDirectory { get; set; } = null!;

    [StringLength(200)]
    public string FileName { get; set; } = null!;

    [StringLength(250)]
    public string? FileTitle { get; set; }

    public string? FileDescription { get; set; }
}
