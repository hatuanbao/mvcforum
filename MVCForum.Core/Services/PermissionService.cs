﻿namespace MvcForum.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Constants;
    using Data.Context;
    using Interfaces;
    using Interfaces.Services;
    using Models.Entities;
    using Utilities;

    public partial class PermissionService : IPermissionService
    {
        private readonly IMvcForumContext _context;
        private readonly ICategoryPermissionForRoleService _categoryPermissionForRoleService;
        private readonly ICacheService _cacheService;

        public PermissionService(ICategoryPermissionForRoleService categoryPermissionForRoleService, IMvcForumContext context, ICacheService cacheService)
        {
            _categoryPermissionForRoleService = categoryPermissionForRoleService;
            _cacheService = cacheService;
            _context = context;
        }

        /// <summary>
        /// Get all permissions
        /// </summary>
        /// <returns></returns>
        public IEnumerable<Permission> GetAll()
        {
            var cacheKey = string.Concat(CacheKeys.Permission.StartsWith, "GetAll");
            return _cacheService.CachePerRequest(cacheKey, () => _context.Permission
                                                                            .AsNoTracking()
                                                                            .OrderBy(x => x.Name)
                                                                            .ToList());
        }

        /// <summary>
        /// Add a new permission
        /// </summary>
        /// <param name="permission"></param>
        public Permission Add(Permission permission)
        {
            permission.Name = StringUtils.SafePlainText(permission.Name);
            return _context.Permission.Add(permission);
        }

        /// <summary>
        ///  Delete permission and associated category permission for roles
        /// </summary>
        /// <param name="permission"></param>
        public void Delete(Permission permission)
        {
            var catPermForRoles = _categoryPermissionForRoleService.GetByPermission(permission.Id);
            foreach (var categoryPermissionForRole in catPermForRoles)
            {
                _categoryPermissionForRoleService.Delete(categoryPermissionForRole);
            }

            _context.Permission.Remove(permission);
        }

        /// <summary>
        /// Get a permision by id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public Permission Get(Guid id)
        {
            var cacheKey = string.Concat(CacheKeys.Permission.StartsWith, "Get-", id);
            return _cacheService.CachePerRequest(cacheKey, () => _context.Permission.FirstOrDefault(x => x.Id == id));
        }
    }
}
