﻿namespace MvcForum.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Data.Entity;
    using System.Data.SqlTypes;
    using System.Drawing;
    using System.Linq;
    using System.Security.Principal;
    using System.Text;
    using System.Threading.Tasks;
    using System.Web;
    using System.Web.Security;
    using Constants;
    using Events;
    using ExtensionMethods;
    using Interfaces;
    using Interfaces.Pipeline;
    using Interfaces.Services;
    using Microsoft.Security.Application;
    using Models.Entities;
    using Models.Enums;
    using Models.General;
    using Newtonsoft.Json;
    using Pipeline;
    using Reflection;
    using Utilities;

    public partial class MembershipService : IMembershipService
    {
        private const int MaxHoursToResetPassword = 48;
        private readonly IActivityService _activityService;
        private readonly ICacheService _cacheService;
        private readonly IMvcForumContext _context;
        private readonly ILocalizationService _localizationService;
        private readonly ISettingsService _settingsService;
        private readonly IVoteService _voteService;
        private readonly IBadgeService _badgeService;
        private readonly ICategoryNotificationService _categoryNotificationService;
        private readonly IPrivateMessageService _privateMessageService;
        private readonly IFavouriteService _favouriteService;
        private readonly ITopicNotificationService _topicNotificationService;
        private readonly IMembershipUserPointsService _membershipUserPointsService;
        private readonly IPollVoteService _pollVoteService;
        private readonly ITopicService _topicService;
        private readonly ICategoryService _categoryService;
        private readonly IPostService _postService;

        /// <summary>
        ///     Constructor
        /// </summary>
        /// <param name="context"></param>
        /// <param name="settingsService"> </param>
        /// <param name="localizationService"> </param>
        /// <param name="activityService"> </param>
        /// <param name="cacheService"></param>
        /// <param name="voteService"></param>
        /// <param name="badgeService"></param>
        /// <param name="categoryNotificationService"></param>
        /// <param name="privateMessageService"></param>
        /// <param name="favouriteService"></param>
        /// <param name="topicNotificationService"></param>
        /// <param name="membershipUserPointsService"></param>
        /// <param name="pollVoteService"></param>
        /// <param name="topicService"></param>
        /// <param name="categoryService"></param>
        /// <param name="postService"></param>
        public MembershipService(IMvcForumContext context, ISettingsService settingsService, ILocalizationService localizationService, 
            IActivityService activityService, ICacheService cacheService, IVoteService voteService, IBadgeService badgeService, 
            ICategoryNotificationService categoryNotificationService, IPrivateMessageService privateMessageService, 
            IFavouriteService favouriteService, ITopicNotificationService topicNotificationService, IMembershipUserPointsService membershipUserPointsService, 
            IPollVoteService pollVoteService, ITopicService topicService, ICategoryService categoryService, IPostService postService)
        {
            _settingsService = settingsService;
            _localizationService = localizationService;
            _activityService = activityService;
            _cacheService = cacheService;
            _voteService = voteService;
            _badgeService = badgeService;
            _categoryNotificationService = categoryNotificationService;
            _privateMessageService = privateMessageService;
            _favouriteService = favouriteService;
            _topicNotificationService = topicNotificationService;
            _membershipUserPointsService = membershipUserPointsService;
            _pollVoteService = pollVoteService;
            _topicService = topicService;
            _categoryService = categoryService;
            _postService = postService;
            _context = context;
        }

        #region Status Codes

        public string ErrorCodeToString(MembershipCreateStatus createStatus)
        {
            // See http://go.microsoft.com/fwlink/?LinkID=177550 for
            // a full list of status codes.
            switch (createStatus)
            {
                case MembershipCreateStatus.DuplicateUserName:
                    return _localizationService.GetResourceString("Members.Errors.DuplicateUserName");

                case MembershipCreateStatus.DuplicateEmail:
                    return _localizationService.GetResourceString("Members.Errors.DuplicateEmail");

                case MembershipCreateStatus.InvalidPassword:
                    return _localizationService.GetResourceString("Members.Errors.InvalidPassword");

                case MembershipCreateStatus.InvalidEmail:
                    return _localizationService.GetResourceString("Members.Errors.InvalidEmail");

                case MembershipCreateStatus.InvalidAnswer:
                    return _localizationService.GetResourceString("Members.Errors.InvalidAnswer");

                case MembershipCreateStatus.InvalidQuestion:
                    return _localizationService.GetResourceString("Members.Errors.InvalidQuestion");

                case MembershipCreateStatus.InvalidUserName:
                    return _localizationService.GetResourceString("Members.Errors.InvalidUserName");

                case MembershipCreateStatus.ProviderError:
                    return _localizationService.GetResourceString("Members.Errors.ProviderError");

                case MembershipCreateStatus.UserRejected:
                    return _localizationService.GetResourceString("Members.Errors.UserRejected");

                default:
                    return _localizationService.GetResourceString("Members.Errors.Unknown");
            }
        }

        #endregion

        public MembershipUser Add(MembershipUser newUser)
        {
            return _context.MembershipUser.Add(newUser);
        }

        public MembershipUser SanitizeUser(MembershipUser membershipUser)
        {
            membershipUser.Avatar = StringUtils.SafePlainText(membershipUser.Avatar);
            membershipUser.Comment = StringUtils.SafePlainText(membershipUser.Comment);
            membershipUser.Email = Sanitizer.GetSafeHtmlFragment(membershipUser.Email);
            membershipUser.Password = Sanitizer.GetSafeHtmlFragment(membershipUser.Password);
            membershipUser.PasswordAnswer = StringUtils.SafePlainText(membershipUser.PasswordAnswer);
            membershipUser.PasswordQuestion = StringUtils.SafePlainText(membershipUser.PasswordQuestion);
            membershipUser.Signature = StringUtils.GetSafeHtml(membershipUser.Signature, true);
            membershipUser.Twitter = StringUtils.SafePlainText(membershipUser.Twitter);
            membershipUser.UserName = StringUtils.SafePlainText(membershipUser.UserName);
            membershipUser.Website = StringUtils.SafePlainText(membershipUser.Website);
            return membershipUser;
        }

        /// <summary>
        ///     Return last login status
        /// </summary>
        public LoginAttemptStatus LastLoginStatus { get; private set; } = LoginAttemptStatus.LoginSuccessful;

        /// <summary>
        ///     Validate a user by password
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="password"></param>
        /// <param name="maxInvalidPasswordAttempts"> </param>
        /// <returns></returns>
        public bool ValidateUser(string userName, string password, int maxInvalidPasswordAttempts)
        {
            userName = Sanitizer.GetSafeHtmlFragment(userName);
            password = Sanitizer.GetSafeHtmlFragment(password);

            LastLoginStatus = LoginAttemptStatus.LoginSuccessful;

            var user = GetUser(userName);

            if (user == null)
            {
                LastLoginStatus = LoginAttemptStatus.UserNotFound;
                return false;
            }

            if (user.IsBanned)
            {
                LastLoginStatus = LoginAttemptStatus.Banned;
                return false;
            }

            if (user.IsLockedOut)
            {
                LastLoginStatus = LoginAttemptStatus.UserLockedOut;
                return false;
            }

            if (!user.IsApproved)
            {
                LastLoginStatus = LoginAttemptStatus.UserNotApproved;
                return false;
            }

            var allowedPasswordAttempts = maxInvalidPasswordAttempts;
            if (user.FailedPasswordAttemptCount >= allowedPasswordAttempts)
            {
                LastLoginStatus = LoginAttemptStatus.PasswordAttemptsExceeded;
                return false;
            }

            var salt = user.PasswordSalt;
            var hash = StringUtils.GenerateSaltedHash(password, salt);
            var passwordMatches = hash == user.Password;

            user.FailedPasswordAttemptCount = passwordMatches ? 0 : user.FailedPasswordAttemptCount + 1;

            if (user.FailedPasswordAttemptCount >= allowedPasswordAttempts)
            {
                user.IsLockedOut = true;
                user.LastLockoutDate = DateTime.UtcNow;
            }

            if (!passwordMatches)
            {
                LastLoginStatus = LoginAttemptStatus.PasswordIncorrect;
                return false;
            }

            return LastLoginStatus == LoginAttemptStatus.LoginSuccessful;
        }

        /// <summary>
        ///     Creates a new, unsaved user, with default (empty) values
        /// </summary>
        /// <returns></returns>
        public MembershipUser CreateEmptyUser()
        {
            var now = DateTime.UtcNow;

            return new MembershipUser
            {
                UserName = string.Empty,
                Password = string.Empty,
                Email = string.Empty,
                PasswordQuestion = string.Empty,
                PasswordAnswer = string.Empty,
                CreateDate = now,
                FailedPasswordAnswerAttempt = 0,
                FailedPasswordAttemptCount = 0,
                LastLockoutDate = (DateTime)SqlDateTime.MinValue,
                LastPasswordChangedDate = now,
                IsApproved = false,
                IsLockedOut = false,
                LastLoginDate = (DateTime)SqlDateTime.MinValue
            };
        }

        /// <summary>
        ///     Create new user
        /// </summary>
        /// <param name="newUser"></param>
        /// <param name="loginType"></param>
        /// <returns></returns>
        public async Task<IPipelineProcess<MembershipUser>> CreateUser(MembershipUser newUser, LoginType loginType)
        {
            // Get the site settings
            var settings = _settingsService.GetSettings(false);

            // Santise the user fields
            newUser = SanitizeUser(newUser);

            // Hash the password
            var salt = StringUtils.CreateSalt(Constants.SaltSize);
            var hash = StringUtils.GenerateSaltedHash(newUser.Password, salt);
            newUser.Password = hash;
            newUser.PasswordSalt = salt;

            // Add the roles
            newUser.Roles = new List<MembershipRole> { settings.NewMemberStartingRole };

            // Set dates
            newUser.CreateDate = newUser.LastPasswordChangedDate = DateTime.UtcNow;
            newUser.LastLockoutDate = (DateTime)SqlDateTime.MinValue;
            newUser.LastLoginDate = DateTime.UtcNow;
            newUser.IsLockedOut = false;
            newUser.Slug = ServiceHelpers.GenerateSlug(newUser.UserName, GetUserBySlugLike(ServiceHelpers.CreateUrl(newUser.UserName)), null);

            // Get the pipelines
            var userCreatePipes = ForumConfiguration.Instance.PipelinesUserCreate;

            // The model to process
            var piplineModel = new PipelineProcess<MembershipUser>(newUser);

            // Add the login type to 
            piplineModel.ExtendedData.Add(Constants.ExtendedDataKeys.LoginType, JsonConvert.SerializeObject(loginType));

            // Get instance of the pipeline to use
            var createUserPipeline = new Pipeline<IPipelineProcess<MembershipUser>, MembershipUser>(_context);

            // Register the pipes 
            var allMembershipUserPipes = ImplementationManager.GetInstances<IPipe<IPipelineProcess<MembershipUser>>>();

            // Loop through the pipes and add the ones we want
            foreach (var pipe in userCreatePipes)
            {
                if (allMembershipUserPipes.ContainsKey(pipe))
                {
                    createUserPipeline.Register(allMembershipUserPipes[pipe]);
                }
            }

            // Process the pipeline
            return await createUserPipeline.Process(piplineModel);
        }

        /// <inheritdoc />
        public async Task<IPipelineProcess<MembershipUser>> EditUser(MembershipUser userToEdit, IPrincipal loggedInUser, Image image)
        {
            // Get the pipelines
            var pipes = ForumConfiguration.Instance.PipelinesUserUpdate;

            // The model to process
            var piplineModel = new PipelineProcess<MembershipUser>(userToEdit);

            // Add the user object
            piplineModel.ExtendedData.Add(Constants.ExtendedDataKeys.Username, loggedInUser.Identity.Name);

            // Add the Image as a base 64 image so we can grab it back out
            if (image != null)
            {
                piplineModel.ExtendedData.Add(Constants.ExtendedDataKeys.ImageBase64, image.ImageToBase64());
                image.Dispose();
            }

            // Get instance of the pipeline to use
            var pipeline = new Pipeline<IPipelineProcess<MembershipUser>, MembershipUser>(_context);

            // Register the pipes 
            var allMembershipUserPipes = ImplementationManager.GetInstances<IPipe<IPipelineProcess<MembershipUser>>>();

            // Loop through the pipes and add the ones we want
            foreach (var pipe in pipes)
            {
                if (allMembershipUserPipes.ContainsKey(pipe))
                {
                    pipeline.Register(allMembershipUserPipes[pipe]);
                }
            }

            // Process the pipeline
            return await pipeline.Process(piplineModel);
        }

        /// <summary>
        ///     Delete a member
        /// </summary>
        /// <param name="user"></param>
        public async Task<IPipelineProcess<MembershipUser>> Delete(MembershipUser user)
        {
            // Get the pipelines
            var pipes = ForumConfiguration.Instance.PipelinesUserDelete;

            // The model to process
            var piplineModel = new PipelineProcess<MembershipUser>(user);

            // Get instance of the pipeline to use
            var pipeline = new Pipeline<IPipelineProcess<MembershipUser>, MembershipUser>(_context);

            // Register the pipes 
            var allMembershipUserPipes = ImplementationManager.GetInstances<IPipe<IPipelineProcess<MembershipUser>>>();

            // Loop through the pipes and add the ones we want
            foreach (var pipe in pipes)
            {
                if (allMembershipUserPipes.ContainsKey(pipe))
                {
                    pipeline.Register(allMembershipUserPipes[pipe]);
                }
            }

            // Process the pipeline
            return await pipeline.Process(piplineModel);
        }

        /// <summary>
        /// Get the member by id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public MembershipUser Get(Guid id)
        {
            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "get-", id);
            return _cacheService.CachePerRequest(cacheKey, () =>
            {
                return _context.MembershipUser
                    .Include(x => x.Roles)
                    .FirstOrDefault(x => x.Id == id);
            });
        }

        /// <summary>
        ///     Get a user by username
        /// </summary>
        /// <param name="username"></param>
        /// <param name="removeTracking"></param>
        /// <returns></returns>
        public MembershipUser GetUser(string username, bool removeTracking = false)
        {
            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "GetUser-", username, "-", removeTracking);
            return _cacheService.CachePerRequest(cacheKey, () =>
            {
                MembershipUser member;

                if (removeTracking)
                {
                    member = _context.MembershipUser
                        .Include(x => x.Roles)
                        .AsNoTracking()
                        .FirstOrDefault(name =>
                            name.UserName.Equals(username, StringComparison.CurrentCultureIgnoreCase));
                }
                else
                {
                    member = _context.MembershipUser
                        .Include(x => x.Roles)
                        .FirstOrDefault(name =>
                            name.UserName.Equals(username, StringComparison.CurrentCultureIgnoreCase));
                }


                // Do a check to log out the user if they are logged in and have been deleted
                if (member == null && HttpContext.Current.User.Identity.Name == username)
                {
                    // Member is null so doesn't exist, yet they are logged in with that username - Log them out
                    FormsAuthentication.SignOut();
                }

                return member;
            });
        }

        /// <summary>
        ///     Get a user by email address
        /// </summary>
        /// <param name="email"></param>
        /// <param name="removeTracking"></param>
        /// <returns></returns>
        public MembershipUser GetUserByEmail(string email, bool removeTracking = false)
        {
            email = StringUtils.SafePlainText(email);
            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "GetUserByEmail-", email, "-", removeTracking);
            return _cacheService.CachePerRequest(cacheKey, () =>
            {
                MembershipUser member;

                if (removeTracking)
                {
                    member = _context.MembershipUser.AsNoTracking()
                        .Include(x => x.Roles)
                        .FirstOrDefault(name => name.Email == email);
                }
                else
                {
                    member = _context.MembershipUser
                        .Include(x => x.Roles)
                        .FirstOrDefault(name => name.Email == email);
                }

                return member;
            });
        }

        /// <summary>
        ///     Get a user by slug
        /// </summary>
        /// <param name="slug"></param>
        /// <returns></returns>
        public MembershipUser GetUserBySlug(string slug)
        {
            slug = StringUtils.GetSafeHtml(slug);

            return _context.MembershipUser
                .Include(x => x.Badges)
                .Include(x => x.Roles)
                .FirstOrDefault(name => name.Slug == slug);
        }

        public IList<MembershipUser> GetUserBySlugLike(string slug)
        {
            slug = StringUtils.GetSafeHtml(slug);
            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "GetUserBySlugLike-", slug);
            return _cacheService.CachePerRequest(cacheKey, () =>
            {
                return _context.MembershipUser
                    .Include(x => x.Roles)
                    .AsNoTracking()
                    .Where(name => name.Slug.ToUpper().Contains(slug.ToUpper()))
                    .ToList();
            });
        }

        /// <summary>
        ///     Get users from a list of Id's
        /// </summary>
        /// <param name="guids"></param>
        /// <returns></returns>
        public IList<MembershipUser> GetUsersById(List<Guid> guids)
        {
            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "GetUsersById-", guids.GetHashCode());
            return _cacheService.CachePerRequest(cacheKey, () =>
            {
                return _context.MembershipUser
                    .Where(x => guids.Contains(x.Id))
                    .AsNoTracking()
                    .ToList();
            });
        }

        /// <summary>
        ///     Get by posts and date
        /// </summary>
        /// <param name="amoutOfDaysSinceRegistered"></param>
        /// <param name="amoutOfPosts"></param>
        /// <returns></returns>
        public IList<MembershipUser> GetUsersByDaysPostsPoints(int amoutOfDaysSinceRegistered, int amoutOfPosts)
        {
            var registerEnd = DateTime.UtcNow;
            var registerStart = registerEnd.AddDays(-amoutOfDaysSinceRegistered);

            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "GetUsersByDaysPostsPoints-",
                amoutOfDaysSinceRegistered, "-", amoutOfPosts);
            return _cacheService.CachePerRequest(cacheKey, () =>
            {
                return _context.MembershipUser
                    .Where(x =>
                        x.Posts.Count <= amoutOfPosts &&
                        x.CreateDate > registerStart &&
                        x.CreateDate <= registerEnd)
                    .ToList();
            });
        }


        /// <summary>
        ///     Return the roles found for this username
        /// </summary>
        /// <param name="username"></param>
        /// <returns></returns>
        public string[] GetRolesForUser(string username)
        {
            username = StringUtils.SafePlainText(username);
            var roles = new List<string>();
            var user = GetUser(username, true);

            if (user != null)
            {
                roles.AddRange(user.Roles.Select(role => role.RoleName));
            }

            return roles.ToArray();
        }

        /// <summary>
        ///     Change the user's password
        /// </summary>
        /// <param name="user"> </param>
        /// <param name="oldPassword"></param>
        /// <param name="newPassword"></param>
        /// <returns></returns>
        public bool ChangePassword(MembershipUser user, string oldPassword, string newPassword)
        {
            oldPassword = StringUtils.SafePlainText(oldPassword);
            newPassword = StringUtils.SafePlainText(newPassword);

            //n3oCacheHelper.Clear(user.UserName);
            var existingUser = Get(user.Id);
            var salt = existingUser.PasswordSalt;
            var oldHash = StringUtils.GenerateSaltedHash(oldPassword, salt);

            if (oldHash != existingUser.Password)
            {
                // Old password is wrong - do not allow update
                return false;
            }

            // Cleared to go ahead with new password
            salt = StringUtils.CreateSalt(Constants.SaltSize);
            var newHash = StringUtils.GenerateSaltedHash(newPassword, salt);

            existingUser.Password = newHash;
            existingUser.PasswordSalt = salt;
            existingUser.LastPasswordChangedDate = DateTime.UtcNow;

            return true;
        }

        /// <summary>
        ///     Reset a users password
        /// </summary>
        /// <param name="user"></param>
        /// <param name="newPassword"> </param>
        /// <returns></returns>
        public bool ResetPassword(MembershipUser user, string newPassword)
        {
            var existingUser = Get(user.Id);

            var salt = StringUtils.CreateSalt(Constants.SaltSize);
            var newHash = StringUtils.GenerateSaltedHash(newPassword, salt);

            existingUser.Password = newHash;
            existingUser.PasswordSalt = salt;
            existingUser.LastPasswordChangedDate = DateTime.UtcNow;

            return true;
        }

        /// <summary>
        ///     Get all members
        /// </summary>
        /// <returns></returns>
        public IList<MembershipUser> GetAll()
        {
            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "GetAll");
            return _cacheService.CachePerRequest(cacheKey, () => _context.MembershipUser.ToList());
        }

        public async Task<PaginatedList<MembershipUser>> GetAll(int pageIndex, int pageSize)
        {
            var query = _context.MembershipUser.OrderBy(x => x.UserName);
            return await PaginatedList<MembershipUser>.CreateAsync(query.AsNoTracking(), pageIndex, pageSize);
        }

        public async Task<PaginatedList<MembershipUser>> SearchMembers(string search, int pageIndex, int pageSize)
        {
            search = StringUtils.SafePlainText(search);

            var query = _context.MembershipUser
                .Where(x => x.UserName.ToUpper().Contains(search.ToUpper()) ||
                            x.Email.ToUpper().Contains(search.ToUpper()))
                .OrderBy(x => x.UserName);

            return await PaginatedList<MembershipUser>.CreateAsync(query.AsNoTracking(), pageIndex, pageSize);
        }

        public IList<MembershipUser> SearchMembers(string username, int amount)
        {
            username = StringUtils.SafePlainText(username);
            return _context.MembershipUser
                .Where(x => x.UserName.ToUpper().Contains(username.ToUpper()))
                .OrderBy(x => x.UserName)
                .Take(amount)
                .ToList();
        }

        public IList<MembershipUser> GetActiveMembers()
        {
            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "GetActiveMembers");
            return _cacheService.CachePerRequest(cacheKey, () =>
            {
                // Get members that last activity date is valid
                var date = DateTime.UtcNow.AddMinutes(-Constants.TimeSpanInMinutesToShowMembers);
                return _context.MembershipUser
                    .Where(x => x.LastActivityDate > date)
                    .AsNoTracking()
                    .ToList();
            });
        }

        /// <summary>
        ///     Get user by id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public MembershipUser GetUser(Guid id)
        {
            return Get(id);
        }

        public IList<MembershipUser> GetLatestUsers(int amountToTake)
        {
            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "GetLatestUsers-", amountToTake);
            return _cacheService.CachePerRequest(cacheKey, () =>
            {
                return _context.MembershipUser.Include(x => x.Roles).AsNoTracking()
                    .OrderByDescending(x => x.CreateDate)
                    .Take(amountToTake)
                    .ToList();
            });
        }

        public IList<MembershipUser> GetLowestPointUsers(int amountToTake)
        {
            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "GetLowestPointUsers-", amountToTake);
            return _cacheService.CachePerRequest(cacheKey, () =>
            {
                return _context.MembershipUser
                    .Join(_context.MembershipUserPoints.AsNoTracking(), // The sequence to join to the first sequence.
                        user => user.Id, // A function to extract the join key from each element of the first sequence.
                        userPoints =>
                            userPoints.User
                                .Id, // A function to extract the join key from each element of the second sequence
                        (user, userPoints) =>
                            new
                            {
                                MembershipUser = user,
                                UserPoints = userPoints
                            } // A function to create a result element from two matching elements.
                    )
                    .AsNoTracking()
                    .OrderBy(x => x.UserPoints)
                    .Take(amountToTake)
                    .Select(t => t.MembershipUser)
                    .ToList();
            });
        }

        public int MemberCount()
        {
            var cacheKey = string.Concat(CacheKeys.Member.StartsWith, "MemberCount");
            return _cacheService.CachePerRequest(cacheKey, () => _context.MembershipUser.AsNoTracking().Count());
        }

        /// <summary>
        ///     Save user (does NOT update password data)
        /// </summary>
        /// <param name="user"></param>
        public void ProfileUpdated(MembershipUser user)
        {
            var e = new UpdateProfileEventArgs { User = user };
            EventManager.Instance.FireBeforeProfileUpdated(this, e);

            if (!e.Cancel)
            {
                EventManager.Instance.FireAfterProfileUpdated(this, new UpdateProfileEventArgs { User = user });
                _activityService.ProfileUpdated(user);
            }
        }

        /// <summary>
        ///     Unlock a user
        /// </summary>
        /// <param name="username"></param>
        /// <param name="resetPasswordAttempts">If true, also reset password attempts to zero</param>
        public void UnlockUser(string username, bool resetPasswordAttempts)
        {
            {
                var user = GetUser(username);

                if (user == null)
                {
                    throw new ApplicationException(_localizationService.GetResourceString("Members.CantUnlock"));
                }

                var existingUser = Get(user.Id);

                user.IsLockedOut = false;
                user.Roles = existingUser.Roles;
                user.Votes = existingUser.Votes;
                user.Password = existingUser.Password;
                user.PasswordSalt = existingUser.PasswordSalt;

                if (resetPasswordAttempts)
                {
                    user.FailedPasswordAnswerAttempt = 0;
                }
            }
        }

        /// <summary>
        ///     Convert all users into CSV format (e.g. for export)
        /// </summary>
        /// <returns></returns>
        public string ToCsv()
        {
            var csv = new StringBuilder();

            foreach (var user in GetAll())
            {
                csv.AppendFormat("{0},{1},{2},{3},{4},{5},{6},{7}", user.UserName, user.Email, user.CreateDate,
                    user.Age,
                    user.Location, user.Website, user.Facebook, user.Signature);
                csv.AppendLine();
            }

            return csv.ToString();
        }

        /// <summary>
        ///     Extract users from CSV format and import them
        /// </summary>
        /// <returns></returns>
        public CsvReport FromCsv(List<string> allLines)
        {
            var usersProcessed = new List<string>();
            var commaSeparator = new[] { ',' };
            var report = new CsvReport();

            if (allLines == null || allLines.Count == 0)
            {
                report.Errors.Add(new CsvErrorWarning
                {
                    ErrorWarningType = CsvErrorWarningType.BadDataFormat,
                    Message = "No users found."
                });
                return report;
            }
            var settings = _settingsService.GetSettings(true);
            var lineCounter = 0;
            foreach (var line in allLines)
            {
                try
                {
                    lineCounter++;

                    // Each line is made up of n items in a predefined order

                    var values = line.Split(commaSeparator);

                    if (values.Length < 2)
                    {
                        report.Errors.Add(new CsvErrorWarning
                        {
                            ErrorWarningType = CsvErrorWarningType.MissingKeyOrValue,
                            Message = $"Line {lineCounter}: insufficient values supplied."
                        });

                        continue;
                    }

                    var userName = values[0];

                    if (userName.IsNullEmpty())
                    {
                        report.Errors.Add(new CsvErrorWarning
                        {
                            ErrorWarningType = CsvErrorWarningType.MissingKeyOrValue,
                            Message = $"Line {lineCounter}: no username supplied."
                        });

                        continue;
                    }

                    var email = values[1];
                    if (email.IsNullEmpty())
                    {
                        report.Errors.Add(new CsvErrorWarning
                        {
                            ErrorWarningType = CsvErrorWarningType.MissingKeyOrValue,
                            Message = $"Line {lineCounter}: no email supplied."
                        });

                        continue;
                    }

                    // get the user
                    var userToImport = GetUser(userName);

                    if (userToImport != null)
                    {
                        report.Errors.Add(new CsvErrorWarning
                        {
                            ErrorWarningType = CsvErrorWarningType.AlreadyExists,
                            Message = $"Line {lineCounter}: user already exists in forum."
                        });

                        continue;
                    }

                    if (usersProcessed.Contains(userName))
                    {
                        report.Errors.Add(new CsvErrorWarning
                        {
                            ErrorWarningType = CsvErrorWarningType.AlreadyExists,
                            Message = $"Line {lineCounter}: user already exists in import file."
                        });

                        continue;
                    }

                    usersProcessed.Add(userName);

                    userToImport = CreateEmptyUser();
                    userToImport.UserName = userName;
                    userToImport.Slug = ServiceHelpers.GenerateSlug(userToImport.UserName,
                        GetUserBySlugLike(ServiceHelpers.CreateUrl(userToImport.UserName)), userToImport.Slug);
                    userToImport.Email = email;
                    userToImport.IsApproved = true;
                    userToImport.PasswordSalt = StringUtils.CreateSalt(Constants.SaltSize);

                    string createDateStr = null;
                    if (values.Length >= 3)
                    {
                        createDateStr = values[2];
                    }
                    userToImport.CreateDate =
                        createDateStr.IsNullEmpty() ? DateTime.UtcNow : DateTime.Parse(createDateStr);

                    if (values.Length >= 4)
                    {
                        userToImport.Age = int.Parse(values[3]);
                    }
                    if (values.Length >= 5)
                    {
                        userToImport.Location = values[4];
                    }
                    if (values.Length >= 6)
                    {
                        userToImport.Website = values[5];
                    }
                    if (values.Length >= 7)
                    {
                        userToImport.Facebook = values[6];
                    }
                    if (values.Length >= 8)
                    {
                        userToImport.Signature = values[7];
                    }
                    userToImport.Roles = new List<MembershipRole> { settings.NewMemberStartingRole };
                    Add(userToImport);
                }
                catch (Exception ex)
                {
                    report.Errors.Add(new CsvErrorWarning
                    {
                        ErrorWarningType = CsvErrorWarningType.GeneralError,
                        Message = ex.Message
                    });
                }
            }

            return report;
        }

        /// <summary>
        /// Scrubs a user, removes everything from points to posts and topics.
        /// </summary>
        /// <param name="user"></param>
        public void ScrubUsers(MembershipUser user)
        {
            // PROFILE
            user.Website = string.Empty;
            user.Twitter = string.Empty;
            user.Facebook = string.Empty;
            user.Avatar = string.Empty;
            user.Signature = string.Empty;

            //// User Votes
            if (user.Votes != null)
            {
                var votesToDelete = new List<Vote>();
                votesToDelete.AddRange(user.Votes);
                votesToDelete.AddRange(user.VotesGiven);
                foreach (var d in votesToDelete)
                {
                    _voteService.Delete(d);
                }
                user.Votes.Clear();
                user.VotesGiven.Clear();
                _context.SaveChanges();
            }

            // User badge time checks
            if (user.BadgeTypesTimeLastChecked != null)
            {
                var toDelete = new List<BadgeTypeTimeLastChecked>();
                toDelete.AddRange(user.BadgeTypesTimeLastChecked);
                foreach (var obj in toDelete)
                {
                    _badgeService.DeleteTimeLastChecked(obj);
                }
                user.BadgeTypesTimeLastChecked.Clear();
                _context.SaveChanges();
            }

            // User Badges
            if (user.Badges != null)
            {
                var toDelete = new List<Badge>();
                toDelete.AddRange(user.Badges);
                foreach (var obj in toDelete)
                {
                    _badgeService.Delete(obj);
                }
                user.Badges.Clear();
                _context.SaveChanges();
            }

            // User category notifications
            if (user.CategoryNotifications != null)
            {
                var toDelete = new List<CategoryNotification>();
                toDelete.AddRange(user.CategoryNotifications);
                foreach (var obj in toDelete)
                {
                    _categoryNotificationService.Delete(obj);
                }
                user.CategoryNotifications.Clear();
                _context.SaveChanges();
            }

            // User PM Received
            if (user.PrivateMessagesReceived != null)
            {
                var toDelete = new List<PrivateMessage>();
                toDelete.AddRange(user.PrivateMessagesReceived);
                foreach (var obj in toDelete)
                {
                    _privateMessageService.DeleteMessage(obj);
                }
                user.PrivateMessagesReceived.Clear();
                _context.SaveChanges();
            }

            // User PM Sent
            if (user.PrivateMessagesSent != null)
            {
                var toDelete = new List<PrivateMessage>();
                toDelete.AddRange(user.PrivateMessagesSent);
                foreach (var obj in toDelete)
                {
                    _privateMessageService.DeleteMessage(obj);
                }
                user.PrivateMessagesSent.Clear();
                _context.SaveChanges();
            }

            // User Favourites
            if (user.Favourites != null)
            {
                var toDelete = new List<Favourite>();
                toDelete.AddRange(user.Favourites);
                foreach (var obj in toDelete)
                {
                    _favouriteService.Delete(obj);
                }
                user.Favourites.Clear();
                _context.SaveChanges();
            }

            if (user.TopicNotifications != null)
            {
                var notificationsToDelete = new List<TopicNotification>();
                notificationsToDelete.AddRange(user.TopicNotifications);
                foreach (var topicNotification in notificationsToDelete)
                {
                    _topicNotificationService.Delete(topicNotification);
                }
                user.TopicNotifications.Clear();
            }

            // Also clear their points
            var userPoints = user.Points;
            if (userPoints.Any())
            {
                var pointsList = new List<MembershipUserPoints>();
                pointsList.AddRange(userPoints);
                foreach (var point in pointsList)
                {
                    point.User = null;
                    _membershipUserPointsService.Delete(point);
                }
                user.Points.Clear();
            }

            // Now clear all activities for this user
            var usersActivities = _activityService.GetDataFieldByGuid(user.Id);
            _activityService.Delete(usersActivities.ToList());
            _context.SaveChanges();

            // Also clear their poll votes
            var userPollVotes = user.PollVotes;
            if (userPollVotes.Any())
            {
                var pollList = new List<PollVote>();
                pollList.AddRange(userPollVotes);
                foreach (var vote in pollList)
                {
                    vote.User = null;
                    _pollVoteService.Delete(vote);
                }
                user.PollVotes.Clear();
                _context.SaveChanges();
            }

            //// ######### POSTS TOPICS ########

            // Delete all topics first
            // This will get rid of everyone elses posts associated with this users topic too
            var topics = user.Topics;
            if (topics != null && topics.Any())
            {
                var topicList = new List<Topic>();
                topicList.AddRange(topics);
                foreach (var topic in topicList)
                {
                    _topicService.Delete(topic);
                }
                user.Topics.Clear();
                _context.SaveChanges();
            }

            // Now sorts Last Posts on topics and delete all the users posts
            var posts = user.Posts;
            if (posts != null && posts.Any())
            {
                var postIds = posts.Select(x => x.Id).ToList();

                // Get all categories
                var allCategories = _categoryService.GetAll();

                // Need to see if any of these are last posts on Topics
                // If so, need to swap out last post
                var lastPostTopics = _topicService.GetTopicsByLastPost(postIds, allCategories.ToList());
                foreach (var topic in lastPostTopics.Where(x => x.User.Id != user.Id))
                {
                    var lastPost = topic.Posts.Where(x => !postIds.Contains(x.Id)).OrderByDescending(x => x.DateCreated).FirstOrDefault();
                    topic.LastPost = lastPost;
                }

                _context.SaveChanges();

                // Delete all posts
                var postList = new List<Post>();
                postList.AddRange(posts);
                foreach (var post in postList)
                {
                    _postService.Delete(post, true);
                }

                user.UploadedFiles.Clear();
                user.Posts.Clear();

                _context.SaveChanges();
            }
        }

        /// <summary>
        ///     Update the user record with a newly generated password reset security token and timestamp
        /// </summary>
        public bool UpdatePasswordResetToken(MembershipUser user)
        {
            var existingUser = GetUser(user.Id);
            if (existingUser == null)
            {
                return false;
            }
            existingUser.PasswordResetToken = CreatePasswordResetToken();
            existingUser.PasswordResetTokenCreatedAt = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        ///     Remove the password reset security token and timestamp from the user record
        /// </summary>
        public bool ClearPasswordResetToken(MembershipUser user)
        {
            var existingUser = GetUser(user.Id);
            if (existingUser == null)
            {
                return false;
            }
            existingUser.PasswordResetToken = null;
            existingUser.PasswordResetTokenCreatedAt = null;
            return true;
        }

        /// <summary>
        ///     To be valid:
        ///     - The user record must contain a password reset token
        ///     - The given token must match the token in the user record
        ///     - The token timestamp must be less than 24 hours ago
        /// </summary>
        public bool IsPasswordResetTokenValid(MembershipUser user, string token)
        {
            var existingUser = GetUser(user.Id);
            if (string.IsNullOrWhiteSpace(existingUser?.PasswordResetToken))
            {
                return false;
            }
            // A security token must have an expiry date
            if (existingUser.PasswordResetTokenCreatedAt == null)
            {
                return false;
            }
            // The security token is only valid for 48 hours
            if ((DateTime.UtcNow - existingUser.PasswordResetTokenCreatedAt.Value).TotalHours >=
                MaxHoursToResetPassword)
            {
                return false;
            }
            return existingUser.PasswordResetToken == token;
        }

        /// <summary>
        ///     Generate a password reset token, a guid is sufficient
        /// </summary>
        private static string CreatePasswordResetToken()
        {
            return Guid.NewGuid().ToString().ToLower().Replace("-", "");
        }
    }
}