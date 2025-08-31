using AutoMapper;
using AccountCore.DAL.Auth.Models;
using AccountCore.DTO.Auth.Entities;
using AccountCore.DTO.Auth.Entities.User;

namespace AccountCore.Services.Auth
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            CreateMap<User, UserDTO>()
                .ForMember(d => d.Roles, map => map.MapFrom(src => src.Roles?.Where(r => r.Enable) ?? new List<RoleUser>()));

            CreateMap<RoleUser, RoleUserDTO>().ReverseMap();
            CreateMap<Role, RoleDTO>().ReverseMap();

            CreateMap<User, UserPostDTO>()
                .ForMember(u => u.RoleIds, map => map.MapFrom(src => src.Roles?.Select(r => r.RoleId).ToArray() ?? Array.Empty<string>()));
            CreateMap<UserPostDTO, User>();

        }
    }
}
