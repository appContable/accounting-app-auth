using AutoMapper;
using AuthDAL.Models;
using AuthDTO.Entities;
using AuthDTO.Entities.User;

namespace AuthServices
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            CreateMap<User, UserDTO>()
                .ForMember(d => d.Roles, map => map.MapFrom(src => src.Roles.Where(r => r.Enable)));

            CreateMap<RoleUser, RoleUserDTO>().ReverseMap();
            CreateMap<Role, RoleDTO>().ReverseMap();

            CreateMap<User, UserPostDTO>()
                .ForMember(u => u.RoleIds, map => map.MapFrom(src => src.Roles.SelectMany(r => r.RoleId).ToArray()));
            CreateMap<UserPostDTO, User>();

        }
    }
}
