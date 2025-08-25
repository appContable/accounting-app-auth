﻿using AccountCore.DTO.Auth.Entities;
using AccountCore.DTO.Auth.Entities.User;
using AccountCore.DTO.Auth.IServices.Result;
using AccountCore.DTO.Auth.Parameters;
using AccountCore.DTO.Auth.ReturnsModels;

namespace AccountCore.DTO.Auth.IServices
{
    public interface IUserService
    {
        /// <summary>
        /// Create a new User
        /// </summary>
        /// <param name="dto">the user</param>
        /// <param name="filterId">filter to invite if apply</param>
        /// <returns>the new users</returns>
        Task<ServiceResult<UserDTO>> Create(UserPostDTO dto);

        /// <summary>
        /// Get a User by Id
        /// </summary>
        /// <param name="id">User Id</param>
        /// <returns>UserDTO</returns>
        Task<ServiceResult<UserDTO>> GetById(string id);

        /// <summary>
        /// Find Uses by Criteria
        /// </summary>
        /// <param name="searchValue">find criteria</param>
        /// <returns>list of users found</returns>
        Task<ServiceResult<IEnumerable<UserDTO>>> Find(string? searchValue);

        /// <summary>
        /// Update an User
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="dto"></param>
        /// <returns></returns>
        Task<ServiceResult<UserDTO>> Update(string userId, UserPostDTO dto);

        /// <summary>
        /// Delete user
        /// </summary>
        /// <param name="userId">user Id</param>
        /// <returns></returns>
        Task<ServiceResult<bool>> Delete(string userId);

        /// <summary>
        /// Enable user
        /// </summary>
        /// <param name="userId">user Id</param>
        /// <returns></returns>
        Task<ServiceResult<bool>> Enable(string userId);

        /// <summary>
        /// Enable user
        /// </summary>
        /// <param name="userId">user Id</param>
        /// <returns></returns>
        Task<ServiceResult<bool>> Disable(string userId);       
    }
}
