using System.ComponentModel.DataAnnotations;

namespace Gbrindes.Web.Controllers;

public sealed class RoleMetadataUpdateRequest
{
    [Required, StringLength(40, MinimumLength = 1, ErrorMessage = "O identificador deve ter entre 1 e 40 caracteres.")]
    [RegularExpression("^[a-z0-9][a-z0-9._-]*$", ErrorMessage = "O identificador deve começar com letra ou número e conter somente letras minúsculas, números, ponto, hífen ou sublinhado.")]
    public string Slug { get; set; } = string.Empty;

    [StringLength(255)]
    public string? Description { get; set; }
}
