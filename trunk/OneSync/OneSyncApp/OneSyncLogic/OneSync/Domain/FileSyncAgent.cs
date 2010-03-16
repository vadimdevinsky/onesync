﻿/*
 $Id$
 */
using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using Community.CsharpSqlite.SQLiteClient;
using Community.CsharpSqlite;

namespace OneSync.Synchronization
{
    public delegate void SyncStartsHandler(object sender, SyncStartsEventArgs eventArgs);
    public delegate void SyncCompletesHandler(object sender, SyncCompletesEventArgs eventArgs);
    public delegate void SyncCancelledHandler(object sender, SyncCancelledEventArgs eventArgs);
    public delegate void SyncStatusChangedHandler(object sender, SyncStatusChangedEventArgs args);
    public delegate void SyncProgressChangedHandler(object sender, SyncProgressChangedEventArgs args);
    public delegate void FileSyncChangedHandler(object sender, FileSyncedChangedEventArgs args);

    public enum SyncStatus
    {
        VERIFY_PATCH,
        APPLY_PATCH,
        GENERATE_PATCH,
        UPDATE_DATA
    }

    /// <summary>
    /// 
    /// </summary>    
    public class FileSyncAgent : BaseSyncAgent
    {
        public event SyncStartsHandler OnStarted;
        public event SyncCompletesHandler OnCompleted;
        public event SyncStatusChangedHandler OnStatusChanged;
        public event SyncProgressChangedHandler OnProgressChanged;
        public event FileSyncChangedHandler OnFileChanged;
        List<SyncAction> actions = new List<SyncAction>();
        private SyncPreviewResult result = null;

        public FileSyncAgent()
            : base()
        {
        }

        public FileSyncAgent(Profile profile)
            : base(profile)
        {
            //actions = (List<SyncAction>)new SQLiteActionProvider(profile).Load(profile.SyncSource, SourceOption.NOT_EQUAL_SOURCE_ID);
            SyncActionsProvider actProvider = SyncClient.GetSyncActionsProvider(profile.IntermediaryStorage.Path);
            actions = (List<SyncAction>)actProvider.Load(profile.SyncSource.ID, true);
        }

        public void Synchronize(SyncPreviewResult preview)
        {
            if (OnStarted != null) OnStarted(this, new SyncStartsEventArgs());
            if (OnProgressChanged != null) OnProgressChanged(this, new SyncProgressChangedEventArgs(0));
            if (OnStatusChanged != null) OnStatusChanged(this, new SyncStatusChangedEventArgs(SyncStatus.APPLY_PATCH));
            Apply();
            if (OnProgressChanged != null) OnProgressChanged(this, new SyncProgressChangedEventArgs(50));
            if (OnStatusChanged != null) OnStatusChanged(this, new SyncStatusChangedEventArgs(SyncStatus.GENERATE_PATCH));
            Generate();
            if (OnProgressChanged != null) OnProgressChanged(this, new SyncProgressChangedEventArgs(100));
            if (OnCompleted != null) OnCompleted(this, new SyncCompletesEventArgs());
        }

        public SyncPreviewResult PreviewSync()
        {
            //Generate current folder's metadata
            //FileMetaData currentItems = (FileMetaData)new SQLiteMetaDataProvider().FromPath(profile.SyncSource);
            FileMetaData currentItems = MetaDataProvider.Generate(profile.SyncSource.Path, profile.SyncSource.ID);

            //Load current folder metadata from database
            MetaDataProvider mdProvider = SyncClient.GetMetaDataProvider(profile.IntermediaryStorage.Path, profile.SyncSource.ID);
            //FileMetaData oldCurrentItems = (FileMetaData)new SQLiteMetaDataProvider(profile).Load(profile.SyncSource, SourceOption.EQUAL_SOURCE_ID);
            FileMetaData oldCurrentItems = mdProvider.Load(profile.SyncSource.ID, false);

            //Load the other folder metadata from database
            //FileMetaData oldOtherItems = (FileMetaData)new SQLiteMetaDataProvider(profile).Load(profile.SyncSource, SourceOption.NOT_EQUAL_SOURCE_ID);
            FileMetaData oldOtherItems = mdProvider.Load(profile.SyncSource.ID, true);


            //By comparing current metadata and the old metadata from previous sync of a sync folder
            //we could know the dirty items of the folder.
            FileMetaDataComparer comparerCurrent = new FileMetaDataComparer(currentItems, oldCurrentItems);
            List<FileMetaDataItem> dirtyItemsInCurrent = new List<FileMetaDataItem>();
            dirtyItemsInCurrent.AddRange(comparerCurrent.LeftOnly);
            dirtyItemsInCurrent.AddRange(comparerCurrent.BothModified);

            //Compare the actions and dirty items in current folder. Collision is detected when 
            //2 items in 2 collection have same relative path but different hashes. 
            IEnumerable<SyncAction> conflictItems = from action in actions
                                                    from dirtyInCurrent in dirtyItemsInCurrent
                                                    where action.RelativeFilePath.Equals(dirtyInCurrent.RelativePath)
                                                    && action.ChangeType == ChangeType.NEWLY_CREATED
                                                    && !action.FileHash.Equals(dirtyInCurrent.HashCode)
                                                    select action;

            foreach (SyncAction action in conflictItems)
            {
                action.ConflictResolution = ConflictResolution.DUPLICATE_RENAME;
            }

            IEnumerable<SyncAction> itemsToDelete = from action in actions
                                                    where action.ChangeType == ChangeType.DELETED
                                                    select action;


            IEnumerable<SyncAction> itemsToCopyOver = from action in actions
                                                      where action.ChangeType == ChangeType.NEWLY_CREATED
                                                      && !conflictItems.Contains(action)
                                                      select action;

            result = new SyncPreviewResult();
            result.ConflictItems = conflictItems.ToList();
            result.ItemsToCopyOver = itemsToCopyOver.ToList();
            result.ItemsToDelete = itemsToDelete.ToList();
            return result;
        }

        public SyncResult Apply()
        {
            SyncResult syncResult = new SyncResult();
            foreach (SyncAction action in result.ItemsToCopyOver)
            {
                if (action.ChangeType == ChangeType.NEWLY_CREATED)
                {
                    if (OnFileChanged != null) OnFileChanged(this, new FileSyncedChangedEventArgs(ChangeType.NEWLY_CREATED, action.RelativeFilePath));
                    try
                    {
                        CopyToSyncFolderAndUpdateActionTable((CreateAction)action, profile);
                        syncResult.Ok.Add(action);
                    }
                    catch (Exception) { }
                }
            }
            foreach (SyncAction action in result.ItemsToDelete)
            {
                if (action.ChangeType == ChangeType.DELETED)
                {
                    if (OnFileChanged != null) OnFileChanged(this, new FileSyncedChangedEventArgs(ChangeType.DELETED, action.RelativeFilePath));
                    try
                    {
                        DeleteInSyncFolderAndUpdateActionTable((DeleteAction)action, profile);
                        syncResult.Ok.Add(action);
                    }
                    catch (Exception)
                    {
                        syncResult.Errors.Add(action);
                    }
                }
            }
            foreach (SyncAction action in result.ConflictItems)
            {
                if (action.ConflictResolution == ConflictResolution.SKIP)
                {
                    syncResult.Skipped.Add(action);
                }
                else if (action.ConflictResolution == ConflictResolution.DUPLICATE_RENAME)
                {
                    if (OnFileChanged != null) OnFileChanged(this, new FileSyncedChangedEventArgs(ChangeType.NEWLY_CREATED, action.RelativeFilePath));
                    try
                    {
                        DuplicateRenameToSyncFolderAndUpdateActionTable((CreateAction)action, profile);
                        syncResult.Ok.Add(action);
                    }
                    catch (Exception)
                    {
                        syncResult.Errors.Add(action);
                    }
                }
                else if (action.ConflictResolution == ConflictResolution.OVERWRITE)
                {
                    if (OnFileChanged != null) OnFileChanged(this, new FileSyncedChangedEventArgs(ChangeType.MODIFIED, action.RelativeFilePath));
                    try
                    {
                        CopyToSyncFolderAndUpdateActionTable((CreateAction)action, profile);
                        syncResult.Ok.Add(action);
                    }
                    catch (Exception)
                    {
                        syncResult.Errors.Add(action);
                    }
                }
            }
            return syncResult;
            // TODO:
            // Update metadata after patch is applied
            // Delete all dirty files
        }

        public void Generate()
        {
            //read metadata of the current folder in file system 
            //FileMetaData currentItems = (FileMetaData)new SQLiteMetaDataProvider().FromPath(profile.SyncSource);
            FileMetaData currentItems = MetaDataProvider.Generate(profile.SyncSource.Path, profile.SyncSource.ID);

            //read metadata of the current folder stored in the database
            MetaDataProvider mdProvider = SyncClient.GetMetaDataProvider(profile.IntermediaryStorage.Path, profile.SyncSource.Path);
            //FileMetaData storedItems = new SQLiteMetaDataProvider(profile).Load(profile.SyncSource, SourceOption.EQUAL_SOURCE_ID);
            FileMetaData storedItems = mdProvider.Load(profile.SyncSource.ID, false);


            //Update metadata 
            //new SQLiteMetaDataProvider().Update(profile, storedItems, currentItems);
            mdProvider.Update(storedItems, currentItems);

            //storedItems = (FileMetaData)new SQLiteMetaDataProvider(profile).Load(profile.SyncSource, SourceOption.NOT_EQUAL_SOURCE_ID);
            storedItems = mdProvider.Load(profile.SyncSource.ID, true);
            FileMetaDataComparer actionGenerator = new FileMetaDataComparer(currentItems, storedItems);

            //generate list of sync actions by comparing 2 metadata

            IList<SyncAction> newActions = actionGenerator.Generate();

            //delete actions of previous sync
            //ActionProcess.DeleteBySourceId(profile, SourceOption.EQUAL_SOURCE_ID);
            SyncActionsProvider actProvider = SyncClient.GetSyncActionsProvider(profile.IntermediaryStorage.Path);
            actProvider.Delete(profile.SyncSource.ID, true);


            if (Directory.Exists(profile.IntermediaryStorage.DirtyFolderPath)) Directory.Delete(profile.IntermediaryStorage.DirtyFolderPath, true);
            foreach (SyncAction action in newActions)
            {
                if (action.ChangeType == ChangeType.NEWLY_CREATED)
                {
                    CopyToDirtyFolderAndUpdateActionTable(action, profile);
                }
                else if (action.ChangeType == ChangeType.DELETED || action.ChangeType == ChangeType.RENAMED)
                {
                    //ActionProcess.InsertAction(action, profile);
                    actProvider.Add(action);
                }
            }

        }

        #region Carryout actions
        public void CopyToDirtyFolderAndUpdateActionTable(SyncAction action, Profile profile)
        {
            // TODO: make it atomic??
            string absolutePathInSyncSource = profile.SyncSource.Path + action.RelativeFilePath;
            string absolutePathInImediateStorage = profile.IntermediaryStorage.DirtyFolderPath + action.RelativeFilePath;

            SyncActionsProvider actProvider = SyncClient.GetSyncActionsProvider(profile.IntermediaryStorage.Path);
            actProvider.Add(action);
            Files.FileUtils.Copy(absolutePathInSyncSource, absolutePathInImediateStorage);

            /*
            string connectionString = string.Format("Version=3,uri=file:{0}", profile.IntermediaryStorage.Path + Configuration.METADATA_RELATIVE_PATH);
            string absolutePathInSyncSource = profile.SyncSource.Path + action.RelativeFilePath;
            string absolutePathInImediateStorage = profile.IntermediaryStorage.DirtyFolderPath + action.RelativeFilePath;
            using (SqliteConnection con = new SqliteConnection(connectionString))
            {
                con.Open();
                SqliteTransaction transaction = (SqliteTransaction)con.BeginTransaction();
                try
                {
                    new SQLiteActionProvider(profile).Insert(action, con);
                    Files.FileUtils.Copy(absolutePathInSyncSource, absolutePathInImediateStorage);
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    throw new DatabaseException();
                }
            }*/
        }

        public void CopyToSyncFolderAndUpdateActionTable(SyncAction action, Profile profile)
        {
            // TODO: atomic....
            string absolutePathInIntermediateStorage = profile.IntermediaryStorage.DirtyFolderPath + action.RelativeFilePath;
            string absolutePathInSyncSource = profile.SyncSource.Path + action.RelativeFilePath;

            SyncActionsProvider actProvider = SyncClient.GetSyncActionsProvider(profile.IntermediaryStorage.Path);
            actProvider.Delete(action);
            
            Files.FileUtils.Copy(absolutePathInIntermediateStorage, absolutePathInSyncSource);
            Files.FileUtils.DeleteFileAndFolderIfEmpty(profile.IntermediaryStorage.DirtyFolderPath, absolutePathInIntermediateStorage);
            
            /*
            string absolutePathInIntermediateStorage = profile.IntermediaryStorage.DirtyFolderPath + action.RelativeFilePath;
            string absolutePathInSyncSource = profile.SyncSource.Path + action.RelativeFilePath;
            string conString1 = string.Format("Version=3,uri=file:{0}", profile.IntermediaryStorage.Path + Configuration.METADATA_RELATIVE_PATH);
            using (SqliteConnection con = new SqliteConnection(conString1))
            {
                con.Open();
                SqliteTransaction transaction = (SqliteTransaction)con.BeginTransaction();
                try
                {
                    new SQLiteActionProvider(profile).Delete(action, con);
                    Files.FileUtils.Copy(absolutePathInIntermediateStorage, absolutePathInSyncSource);
                    Files.FileUtils.DeleteFileAndFolderIfEmpty(profile.IntermediaryStorage.DirtyFolderPath, absolutePathInIntermediateStorage);
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    throw new DatabaseException();
                }
            }*/
        }

        public void DuplicateRenameToSyncFolderAndUpdateActionTable(SyncAction action, Profile profile)
        {
            string absolutePathInIntermediateStorage = profile.IntermediaryStorage.DirtyFolderPath + action.RelativeFilePath;
            string absolutePathInSyncSource = profile.SyncSource.Path + action.RelativeFilePath;

            SyncActionsProvider actProvider = SyncClient.GetSyncActionsProvider(profile.IntermediaryStorage.Path);
            actProvider.Delete(action);

            Files.FileUtils.DuplicateRename(absolutePathInIntermediateStorage, absolutePathInSyncSource);
            Files.FileUtils.DeleteFileAndFolderIfEmpty(profile.IntermediaryStorage.DirtyFolderPath, absolutePathInIntermediateStorage);

            /*
            string absolutePathInIntermediateStorage = profile.IntermediaryStorage.DirtyFolderPath + action.RelativeFilePath;
            string absolutePathInSyncSource = profile.SyncSource.Path + action.RelativeFilePath;
            string conString1 = string.Format("Version=3,uri=file:{0}", profile.IntermediaryStorage.Path + Configuration.METADATA_RELATIVE_PATH);
            using (SqliteConnection con = new SqliteConnection(conString1))
            {
                con.Open();
                SqliteTransaction transaction = (SqliteTransaction)con.BeginTransaction();
                try
                {
                    new SQLiteActionProvider(profile).Delete(action, con);
                    Files.FileUtils.DuplicateRename(absolutePathInIntermediateStorage, absolutePathInSyncSource);
                    Files.FileUtils.DeleteFileAndFolderIfEmpty(profile.IntermediaryStorage.DirtyFolderPath, absolutePathInIntermediateStorage);
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    throw new DatabaseException();
                }
            }*/
        }

        public void DeleteInSyncFolderAndUpdateActionTable(SyncAction action, Profile profile)
        {
            string absolutePathInSyncSource = profile.SyncSource.Path + action.RelativeFilePath;

            SyncActionsProvider actProvider = SyncClient.GetSyncActionsProvider(profile.IntermediaryStorage.Path);
            actProvider.Delete(action);

            Files.FileUtils.DeleteFileAndFolderIfEmpty(profile.SyncSource.Path, absolutePathInSyncSource);

            /*
            string absolutePathInSyncSource = profile.SyncSource.Path + action.RelativeFilePath;
            string conString1 = string.Format("Version=3,uri=file:{0}", profile.IntermediaryStorage.Path + Configuration.METADATA_RELATIVE_PATH);
            using (SqliteConnection con = new SqliteConnection(conString1))
            {
                con.Open();
                SqliteTransaction transaction = (SqliteTransaction)con.BeginTransaction();
                try
                {
                    new SQLiteActionProvider(profile).Delete(action, con);
                    Files.FileUtils.DeleteFileAndFolderIfEmpty(profile.SyncSource.Path, absolutePathInSyncSource);
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    throw new DatabaseException();
                }
            }*/
        }

        public void RenameInSyncFolderAndUpdateActionTable(RenameAction action, Profile profile)
        {
            string oldAbsolutePathInSyncSource = profile.SyncSource.Path + action.PreviousRelativeFilePath;
            string newAbsolutePathInSyncSource = profile.SyncSource.Path + action.RelativeFilePath;

            SyncActionsProvider actProvider = SyncClient.GetSyncActionsProvider(profile.IntermediaryStorage.Path);
            actProvider.Delete(action);

            Files.FileUtils.Copy(oldAbsolutePathInSyncSource, newAbsolutePathInSyncSource);

            /*
            string oldAbsolutePathInSyncSource = profile.SyncSource.Path + action.PreviousRelativeFilePath;
            string newAbsolutePathInSyncSource = profile.SyncSource.Path + action.RelativeFilePath;
            string conString1 = string.Format("Version=3,uri=file:{0}", profile.IntermediaryStorage.Path + Configuration.METADATA_RELATIVE_PATH);
            using (SqliteConnection con = new SqliteConnection(conString1))
            {
                con.Open();
                SqliteTransaction transaction = (SqliteTransaction)con.BeginTransaction();
                try
                {
                    new SQLiteActionProvider(profile).Delete(action, con);
                    Files.FileUtils.Copy(oldAbsolutePathInSyncSource, newAbsolutePathInSyncSource);
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    throw new DatabaseException();
                }
            }
             */
        }
        #endregion Carryout actions
    }
}