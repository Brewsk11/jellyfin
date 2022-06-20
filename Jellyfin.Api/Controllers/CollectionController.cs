using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.ModelBinders;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Collections;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers
{
    /// <summary>
    /// The collection controller.
    /// </summary>
    [Route("Collections")]
    [Authorize(Policy = Policies.DefaultAuthorization)]
    public class CollectionController : BaseJellyfinApiController
    {
        private readonly IUserManager _userManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IDtoService _dtoService;
        private readonly IAuthorizationContext _authContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="CollectionController"/> class.
        /// </summary>
        /// <param name="userManager">Instance of <see cref="IUserManager"/> interface.</param>
        /// <param name="collectionManager">Instance of <see cref="ICollectionManager"/> interface.</param>
        /// <param name="dtoService">Instance of <see cref="IDtoService"/> interface.</param>
        /// <param name="authContext">Instance of <see cref="IAuthorizationContext"/> interface.</param>
        public CollectionController(
            IUserManager userManager,
            ICollectionManager collectionManager,
            IDtoService dtoService,
            IAuthorizationContext authContext)
        {
            _userManager = userManager;
            _collectionManager = collectionManager;
            _dtoService = dtoService;
            _authContext = authContext;
        }

        /// <summary>
        /// Creates a new collection.
        /// </summary>
        /// <param name="name">The name of the collection.</param>
        /// <param name="ids">Item Ids to add to the collection.</param>
        /// <param name="parentId">Optional. Create the collection within a specific folder.</param>
        /// <param name="isLocked">Whether or not to lock the new collection.</param>
        /// <response code="200">Collection created.</response>
        /// <returns>A <see cref="CollectionCreationOptions"/> with information about the new collection.</returns>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<CollectionCreationResult>> CreateCollection(
            [FromQuery] string? name,
            [FromQuery, ModelBinder(typeof(CommaDelimitedArrayModelBinder))] string[] ids,
            [FromQuery] Guid? parentId,
            [FromQuery] bool isLocked = false)
        {
            var userId = (await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false)).UserId;

            var item = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                IsLocked = isLocked,
                Name = name,
                ParentId = parentId,
                ItemIdList = ids,
                UserIds = new[] { userId }
            }).ConfigureAwait(false);

            var dtoOptions = new DtoOptions().AddClientFields(Request);

            var dto = _dtoService.GetBaseItemDto(item, dtoOptions);

            return new CollectionCreationResult
            {
                Id = dto.Id
            };
        }

        /// <summary>
        /// Adds items to a collection.
        /// </summary>
        /// <param name="collectionId">The collection id.</param>
        /// <param name="ids">Item ids, comma delimited.</param>
        /// <response code="204">Items added to collection.</response>
        /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
        [HttpPost("{collectionId}/Items")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> AddToCollection(
            [FromRoute, Required] Guid collectionId,
            [FromQuery, Required, ModelBinder(typeof(CommaDelimitedArrayModelBinder))] Guid[] ids)
        {
            await _collectionManager.AddToCollectionAsync(collectionId, ids).ConfigureAwait(true);
            return NoContent();
        }

        /// <summary>
        /// Removes items from a collection.
        /// </summary>
        /// <param name="collectionId">The collection id.</param>
        /// <param name="ids">Item ids, comma delimited.</param>
        /// <response code="204">Items removed from collection.</response>
        /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
        [HttpDelete("{collectionId}/Items")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> RemoveFromCollection(
            [FromRoute, Required] Guid collectionId,
            [FromQuery, Required, ModelBinder(typeof(CommaDelimitedArrayModelBinder))] Guid[] ids)
        {
            await _collectionManager.RemoveFromCollectionAsync(collectionId, ids).ConfigureAwait(false);
            return NoContent();
        }

        /// <summary>
        /// Gets a list of next up movies in collection.
        /// </summary>
        /// <param name="userId">The user id of the user to get the next up episodes for.</param>
        /// <param name="startIndex">Optional. The record index to start at. All items with a lower index will be dropped from the results.</param>
        /// <param name="limit">Optional. The maximum number of records to return.</param>
        /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
        /// <param name="seriesId">Optional. Filter by series id.</param>
        /// <param name="parentId">Optional. Specify this to localize the search to a specific item or folder. Omit to use the root.</param>
        /// <param name="enableImages">Optional. Include image information in output.</param>
        /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
        /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
        /// <param name="enableUserData">Optional. Include user data.</param>
        /// <param name="nextUpDateCutoff">Optional. Starting date of shows to show in Next Up section.</param>
        /// <param name="enableTotalRecordCount">Whether to enable the total records count. Defaults to true.</param>
        /// <param name="disableFirstEpisode">Whether to disable sending the first episode in a series as next up.</param>
        /// <param name="enableRewatching">Whether to include watched episode in next up results.</param>
        /// <returns>A <see cref="QueryResult{BaseItemDto}"/> with the next up episodes.</returns>
        [HttpGet("NextUp")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<QueryResult<BaseItemDto>> GetNextUp(
            [FromQuery] Guid? userId,
            [FromQuery] int? startIndex,
            [FromQuery] int? limit,
            [FromQuery, ModelBinder(typeof(CommaDelimitedArrayModelBinder))] ItemFields[] fields,
            [FromQuery] string? seriesId,
            [FromQuery] Guid? parentId,
            [FromQuery] bool? enableImages,
            [FromQuery] int? imageTypeLimit,
            [FromQuery, ModelBinder(typeof(CommaDelimitedArrayModelBinder))] ImageType[] enableImageTypes,
            [FromQuery] bool? enableUserData,
            [FromQuery] DateTime? nextUpDateCutoff,
            [FromQuery] bool enableTotalRecordCount = true,
            [FromQuery] bool disableFirstEpisode = false,
            [FromQuery] bool enableRewatching = false)
        {
            var options = new DtoOptions { Fields = fields }
                .AddClientFields(Request)
                .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);

            var result = _collectionManager.GetNextUp(
                new NextUpMoviesQuery
                {
                    Limit = limit,
                    ParentId = parentId,
                    SeriesId = seriesId,
                    StartIndex = startIndex,
                    UserId = userId ?? Guid.Empty,
                    EnableTotalRecordCount = enableTotalRecordCount,
                    DisableFirstEpisode = disableFirstEpisode,
                    NextUpDateCutoff = nextUpDateCutoff ?? DateTime.MinValue,
                    EnableRewatching = enableRewatching
                },
                options);

            var user = userId is null || userId.Value.Equals(default)
                ? null
                : _userManager.GetUserById(userId.Value);

            var returnItems = _dtoService.GetBaseItemDtos(result.Items, options, user);

            return new QueryResult<BaseItemDto>(
                startIndex,
                result.TotalRecordCount,
                returnItems);
        }
    }
}
