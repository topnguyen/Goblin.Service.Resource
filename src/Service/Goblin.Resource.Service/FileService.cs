﻿using System;
using System.IO;
using Elect.DI.Attributes;
using Goblin.Resource.Contract.Repository.Interfaces;
using Goblin.Resource.Contract.Service;
using System.Threading;
using System.Threading.Tasks;
using Elect.Core.SecurityUtils;
using Elect.Data.IO.FileUtils;
using Elect.Data.IO.ImageUtils;
using Elect.Mapper.AutoMapper.IQueryableUtils;
using Elect.Mapper.AutoMapper.ObjUtils;
using Elect.Web.StringUtils;
using Goblin.Resource.Contract.Repository.Models;
using Goblin.Resource.Core;
using Goblin.Resource.Share.Models;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Resource.Service
{
    [ScopedDependency(ServiceType = typeof(IFileService))]
    public class FileService : Base.Service, IFileService
    {
        private readonly IGoblinRepository<FileEntity> _fileRepo;

        public FileService(IGoblinUnitOfWork goblinUnitOfWork, IGoblinRepository<FileEntity> fileRepo) : base(goblinUnitOfWork)
        {
            _fileRepo = fileRepo;
        }

        public async Task<GoblinResourceFileModel> SaveAsync(GoblinResourceUploadFileModel model, CancellationToken cancellationToken = default)
        {

            FileServiceHelper.Correct(model);

            var fileEntity = model.MapTo<FileEntity>();

            fileEntity.Hash = SecurityHelper.EncryptSha256(model.ContentBase64);
            
            var fileBytes = Convert.FromBase64String(model.ContentBase64);

            var imageInfo = ImageHelper.GetImageInfo(fileBytes);

            // Fill information based on File is Image or not

            FileServiceHelper.FillInformation(imageInfo, fileEntity);

            cancellationToken.ThrowIfCancellationRequested();
            
            string fileName;

            // Save Files

            if (imageInfo != null)
            {
                // Main Image

                var isNeedResizeImage = model.ImageMaxHeightPx < fileEntity.ImageHeightPx ||
                                        model.ImageMaxWidthPx < fileEntity.ImageWidthPx;

                fileBytes = FileServiceHelper.ResizeAndCompressImage(fileBytes,
                    isNeedResizeImage,
                    model.ImageMaxWidthPx.Value,
                    model.ImageMaxHeightPx.Value,
                    model.IsEnableCompressImage);

                if (model.IsEnableCompressImage)
                {
                    fileEntity.IsCompressedImage = true;
                }

                // Refill information after resize and compress

                if (isNeedResizeImage || model.IsEnableCompressImage)
                {
                    var newImageInfo = ImageHelper.GetImageInfo(fileBytes);
                    FileServiceHelper.FillInformation(newImageInfo, fileEntity);
                }
                
                // Save File
                
                fileName = FileServiceHelper.GenerateFileName(fileEntity.Name, imageInfo);
                
                fileEntity.Slug = FileServiceHelper.SaveFile(fileBytes, model.Folder, fileName, string.Empty, fileEntity.Extension);
                
                fileEntity.ContentLength = fileBytes.Length;
                
                // --------------------------
                //    Save More Image Size
                // --------------------------

                // Image Skeleton

                var imageSkeletonBytes = FileServiceHelper.ResizeAndCompressImage(fileBytes,
                    true,
                    SystemSetting.Current.ImageSkeletonMaxWidthPx,
                    SystemSetting.Current.ImageSkeletonMaxHeightPx,
                    true);
                
                FileServiceHelper.SaveFile(imageSkeletonBytes, model.Folder, fileName, "-s", fileEntity.Extension);

                // Image Thumbnail

                var imageThumbnailBytes = FileServiceHelper.ResizeAndCompressImage(fileBytes,
                    true,
                    SystemSetting.Current.ImageThumbnailMaxWidthPx,
                    SystemSetting.Current.ImageThumbnailMaxHeightPx,
                    true);

                FileServiceHelper.SaveFile(imageThumbnailBytes, model.Folder, fileName, "-t", fileEntity.Extension);
            }
            else
            {
                fileName = FileServiceHelper.GenerateFileName(fileEntity.Name, null);
                
                fileEntity.Slug = FileServiceHelper.SaveFile(fileBytes, model.Folder, fileName, string.Empty, fileEntity.Extension);

                fileEntity.ContentLength = fileBytes.Length;
            }
            
            _fileRepo.Add(fileEntity);

            await GoblinUnitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(true);

            // Response
            
            var fileModel = fileEntity.MapTo<GoblinResourceFileModel>();

            return fileModel;
        }

        public async Task<GoblinResourceFileModel> GetAsync(string slug, CancellationToken cancellationToken = default)
        {
            slug = slug?.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(slug))
            {
                return null;
            }

            var fileModel =
                await _fileRepo
                    .Get(x => x.Slug == slug)
                    .QueryTo<GoblinResourceFileModel>()
                    .FirstOrDefaultAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(true);

            return fileModel;
        }

        public async Task DeleteAsync(string slug, CancellationToken cancellationToken = default)
        {
            slug = slug?.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(slug))
            {
                return;
            }

            var fileEntity =
                await _fileRepo
                    .Get(x => x.Slug == slug)
                    .FirstOrDefaultAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(true);

            if (fileEntity != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                _fileRepo.Delete(fileEntity, true);
                
                // Delete Physical File

                var filePath = Path.Combine(Directory.GetCurrentDirectory(), SystemSetting.Current.ResourceFolderPath, fileEntity.Slug);

                FileHelper.SafeDelete(filePath);
                
                // Save Change
                
                await GoblinUnitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
            }
        }
    }
}