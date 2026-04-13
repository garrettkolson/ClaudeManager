using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeManager.Hub.Persistence.Entities;

/// <summary>
/// Stores 768-dimensional embedding vectors for wiki entries.
/// Represents the ViaVectorIndex table with all required columns: id, sessionId, embedding, createdAt.
/// </summary>
[Table("ViaVectorIndex")]
public class ViaVectorIndex
{
    /// <summary>
    /// Primary key identifier for the vector entry.
    /// </summary>
    [Key]
    [Column(Order = 0)]
    public long Id { get; set; }

    /// <summary>
    /// The parent wiki entry ID (1-based). Used to join with WikiEntryEntity.id.
    /// Represents the sessionId column from AC-1 specification.
    /// </summary>
    [Column(Order = 1)]
    [MaxLength(50)]
    public string SessionId { get; set; } = default!;

    /// <summary>
    /// 768-dimensional sentence-transformers embedding vector.
    /// Stored as float array for efficient similarity computation.
    /// </summary>
    [Column(TypeName = "REAL[]")]
    [MaxLength(768)]
    public float[] Embedding { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Timestamp when the vector entry was created.
    /// </summary>
    [Column(TypeName = "datetimeoffset")]
    [Column(Order = 2)]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
