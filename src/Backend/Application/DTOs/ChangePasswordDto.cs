using System;
using System.Collections.Generic;
using System.Text;

namespace Application.DTOs;

public class ChangePasswordDto
{
    public string CurrentPassword { get; set; } = null!;
    public string NewPassword { get; set; } = null!;
}
