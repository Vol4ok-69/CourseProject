using System;
using System.Collections.Generic;
using System.Text;

namespace Application.DTOs;

public class UserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = null!;
    public int RoleId { get; set; }
    public string RoleName { get; set; } = null!;
}