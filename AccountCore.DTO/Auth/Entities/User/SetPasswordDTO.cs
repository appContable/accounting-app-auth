﻿namespace AccountCore.DTO.Auth.Entities.User
{
    public class SetPasswordDTO
    {
        public string? Password { get; set; }

        public string? ConfirmPassword { get; set; }
    }
}
