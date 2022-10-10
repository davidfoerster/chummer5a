/*  This file is part of Chummer5a.
 *
 *  Chummer5a is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  Chummer5a is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with Chummer5a.  If not, see <http://www.gnu.org/licenses/>.
 *
 *  You can obtain the full source code for Chummer5a at
 *  https://github.com/chummer5a/chummer5a
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.XPath;
using NLog;

namespace Chummer
{
    public partial class EditCharacterSettings : Form
    {
        private static readonly Lazy<Logger> s_ObjLogger = new Lazy<Logger>(LogManager.GetCurrentClassLogger);
        private static Logger Log => s_ObjLogger.Value;
        private readonly CharacterSettings _objCharacterSettings;
        private CharacterSettings _objReferenceCharacterSettings;
        private readonly List<ListItem> _lstSettings = Utils.ListItemListPool.Get();

        // List of custom data directory infos on the character, in load order. If the character has a directory name for which we have no info, key will be a string instead of an info
        private readonly TypedOrderedDictionary<object, bool> _dicCharacterCustomDataDirectoryInfos = new TypedOrderedDictionary<object, bool>();

        private bool _blnLoading = true;
        private bool _blnSkipLimbCountUpdate;
        private bool _blnDirty;
        private bool _blnSourcebookToggle = true;
        private bool _blnWasRenamed;
        private bool _blnIsLayoutSuspended = true;
        private bool _blnForceMasterIndexRepopulateOnClose;

        // Used to revert to old selected setting if user cancels out of selecting a different one
        private int _intOldSelectedSettingIndex = -1;

        private readonly HashSet<string> _setPermanentSourcebooks = Utils.StringHashSetPool.Get();

        #region Form Events

        public EditCharacterSettings(CharacterSettings objExistingSettings = null)
        {
            InitializeComponent();
            this.UpdateLightDarkMode();
            this.TranslateWinForm();
            _objReferenceCharacterSettings = objExistingSettings;
            if (_objReferenceCharacterSettings == null)
            {
                if (SettingsManager.LoadedCharacterSettings.TryGetValue(GlobalSettings.DefaultCharacterSetting,
                                                                        out CharacterSettings objSetting))
                    _objReferenceCharacterSettings = objSetting;
                else if (SettingsManager.LoadedCharacterSettings.TryGetValue(
                    GlobalSettings.DefaultCharacterSettingDefaultValue,
                    out objSetting))
                    _objReferenceCharacterSettings = objSetting;
                else
                    _objReferenceCharacterSettings = SettingsManager.LoadedCharacterSettings.Values.First();
            }
            _objCharacterSettings = new CharacterSettings(_objReferenceCharacterSettings);
            _objCharacterSettings.PropertyChanged += SettingsChanged;
            Disposed += (sender, args) =>
            {
                _objCharacterSettings.PropertyChanged -= SettingsChanged;
                _objCharacterSettings.Dispose();
                Utils.ListItemListPool.Return(_lstSettings);
                Utils.StringHashSetPool.Return(_setPermanentSourcebooks);
            };
        }

        private async void EditCharacterSettings_Load(object sender, EventArgs e)
        {
            await RebuildCustomDataDirectoryInfosAsync().ConfigureAwait(false);
            await SetToolTips().ConfigureAwait(false);
            await PopulateSettingsList().ConfigureAwait(false);

            using (new FetchSafelyFromPool<List<ListItem>>(Utils.ListItemListPool, out List<ListItem> lstBuildMethods))
            {
                lstBuildMethods.Add(new ListItem(CharacterBuildMethod.Priority, await LanguageManager.GetStringAsync("String_Priority").ConfigureAwait(false)));
                lstBuildMethods.Add(new ListItem(CharacterBuildMethod.SumtoTen, await LanguageManager.GetStringAsync("String_SumtoTen").ConfigureAwait(false)));
                lstBuildMethods.Add(new ListItem(CharacterBuildMethod.Karma, await LanguageManager.GetStringAsync("String_Karma").ConfigureAwait(false)));
                if (GlobalSettings.LifeModuleEnabled)
                    lstBuildMethods.Add(new ListItem(CharacterBuildMethod.LifeModule,
                                                     await LanguageManager.GetStringAsync("String_LifeModule").ConfigureAwait(false)));

                await cboBuildMethod.PopulateWithListItemsAsync(lstBuildMethods).ConfigureAwait(false);
            }

            await PopulateOptions().ConfigureAwait(false);
            SetupDataBindings();

            await SetIsDirty(false).ConfigureAwait(false);
            _blnLoading = false;
            _blnIsLayoutSuspended = false;
        }

        #endregion Form Events

        #region Control Events

        private async void cmdGlobalOptionsCustomData_Click(object sender, EventArgs e)
        {
            CursorWait objCursorWait = await CursorWait.NewAsync(this).ConfigureAwait(false);
            try
            {
                using (ThreadSafeForm<EditGlobalSettings> frmOptions =
                       await ThreadSafeForm<EditGlobalSettings>.GetAsync(() =>
                                                                             new EditGlobalSettings(
                                                                                 "tabCustomDataDirectories")).ConfigureAwait(false))
                    await frmOptions.ShowDialogSafeAsync(this).ConfigureAwait(false);
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async void cmdRename_Click(object sender, EventArgs e)
        {
            string strRename = await LanguageManager.GetStringAsync("Message_CharacterOptions_SettingRename").ConfigureAwait(false);
            using (ThreadSafeForm<SelectText> frmSelectName = await ThreadSafeForm<SelectText>.GetAsync(() => new SelectText
                   {
                       DefaultString = _objCharacterSettings.Name,
                       Description = strRename
                   }).ConfigureAwait(false))
            {
                if (await frmSelectName.ShowDialogSafeAsync(this).ConfigureAwait(false) != DialogResult.OK)
                    return;
                _objCharacterSettings.Name = frmSelectName.MyForm.SelectedValue;
            }

            CursorWait objCursorWait = await CursorWait.NewAsync(this).ConfigureAwait(false);
            try
            {
                bool blnDoResumeLayout = !_blnIsLayoutSuspended;
                if (blnDoResumeLayout)
                {
                    _blnIsLayoutSuspended = true;
                    await this.DoThreadSafeAsync(x => x.SuspendLayout()).ConfigureAwait(false);
                }

                try
                {
                    int intCurrentSelectedSettingIndex = await cboSetting.DoThreadSafeFuncAsync(x => x.SelectedIndex).ConfigureAwait(false);
                    if (intCurrentSelectedSettingIndex >= 0)
                    {
                        ListItem objNewListItem = new ListItem(_lstSettings[intCurrentSelectedSettingIndex].Value,
                                                               _objCharacterSettings.DisplayName);
                        _blnLoading = true;
                        try
                        {
                            _lstSettings[intCurrentSelectedSettingIndex] = objNewListItem;
                            await cboSetting.PopulateWithListItemsAsync(_lstSettings).ConfigureAwait(false);
                            await cboSetting.DoThreadSafeAsync(x => x.SelectedIndex = intCurrentSelectedSettingIndex).ConfigureAwait(false);
                        }
                        finally
                        {
                            _blnLoading = false;
                        }
                    }

                    _blnWasRenamed = true;
                    await SetIsDirty(true).ConfigureAwait(false);
                }
                finally
                {
                    if (blnDoResumeLayout)
                    {
                        _blnIsLayoutSuspended = false;
                        await this.DoThreadSafeAsync(x => x.ResumeLayout()).ConfigureAwait(false);
                    }
                }

                _intOldSelectedSettingIndex = await cboSetting.DoThreadSafeFuncAsync(x => x.SelectedIndex).ConfigureAwait(false);
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async void cmdDelete_Click(object sender, EventArgs e)
        {
            // Verify that the user wants to delete this setting
            if (Program.ShowMessageBox(
                string.Format(GlobalSettings.CultureInfo, await LanguageManager.GetStringAsync("Message_CharacterOptions_ConfirmDelete").ConfigureAwait(false),
                    _objReferenceCharacterSettings.Name),
                await LanguageManager.GetStringAsync("MessageTitle_CharacterOptions_ConfirmDelete").ConfigureAwait(false),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            CursorWait objCursorWait = await CursorWait.NewAsync(this).ConfigureAwait(false);
            try
            {
                LockingDictionary<string, CharacterSettings> dicCharacterSettings = await SettingsManager.GetLoadedCharacterSettingsAsModifiableAsync().ConfigureAwait(false);
                (bool blnSuccess, CharacterSettings objDeletedSettings)
                    = await dicCharacterSettings.TryRemoveAsync(
                        _objReferenceCharacterSettings.DictionaryKey).ConfigureAwait(false);
                if (!blnSuccess)
                    return;
                if (!await Utils.SafeDeleteFileAsync(
                        Path.Combine(Utils.GetStartupPath, "settings", _objReferenceCharacterSettings.FileName), true).ConfigureAwait(false))
                {
                    // Revert removal of setting if we cannot delete the file
                    await dicCharacterSettings.AddAsync(
                        objDeletedSettings.DictionaryKey, objDeletedSettings).ConfigureAwait(false);
                    return;
                }

                // Force repopulate character settings list in Master Index from here in lieu of event handling for concurrent dictionaries
                _blnForceMasterIndexRepopulateOnClose = true;
                KeyValuePair<string, CharacterSettings> kvpReplacementOption
                    = await dicCharacterSettings.FirstOrDefaultAsync(
                        x => x.Value.BuiltInOption
                             && x.Value.BuildMethod == _objReferenceCharacterSettings.BuildMethod).ConfigureAwait(false);
                await Program.OpenCharacters.ForEachAsync(async objCharacter =>
                {
                    if (await objCharacter.GetSettingsKeyAsync().ConfigureAwait(false) == _objReferenceCharacterSettings.FileName)
                        await objCharacter.SetSettingsKeyAsync(kvpReplacementOption.Key).ConfigureAwait(false);
                }).ConfigureAwait(false);
                bool blnDoResumeLayout = !_blnIsLayoutSuspended;
                if (blnDoResumeLayout)
                {
                    _blnIsLayoutSuspended = true;
                    await this.DoThreadSafeAsync(x => x.SuspendLayout()).ConfigureAwait(false);
                }

                try
                {
                    _objReferenceCharacterSettings = kvpReplacementOption.Value;
                    await _objCharacterSettings.CopyValuesAsync(_objReferenceCharacterSettings).ConfigureAwait(false);
                    await RebuildCustomDataDirectoryInfosAsync().ConfigureAwait(false);
                    await SetIsDirty(false).ConfigureAwait(false);
                    await PopulateSettingsList().ConfigureAwait(false);
                }
                finally
                {
                    if (blnDoResumeLayout)
                    {
                        _blnIsLayoutSuspended = false;
                        await this.DoThreadSafeAsync(x => x.ResumeLayout()).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async void cmdSaveAs_Click(object sender, EventArgs e)
        {
            string strSelectedName;
            string strSelectedFullFileName;
            string strSelectSettingName
                = await LanguageManager.GetStringAsync("Message_CharacterOptions_SelectSettingName").ConfigureAwait(false);
            LockingDictionary<string, CharacterSettings> dicCharacterSettings = await SettingsManager.GetLoadedCharacterSettingsAsModifiableAsync().ConfigureAwait(false);
            do
            {
                do
                {
                    using (ThreadSafeForm<SelectText> frmSelectName = await ThreadSafeForm<SelectText>.GetAsync(() => new SelectText
                           {
                               DefaultString = _objCharacterSettings.BuiltInOption
                                   ? string.Empty
                                   : _objCharacterSettings.FileName.TrimEndOnce(".xml"),
                               Description = strSelectSettingName
                           }).ConfigureAwait(false))
                    {
                        if (await frmSelectName.ShowDialogSafeAsync(this).ConfigureAwait(false) != DialogResult.OK)
                            return;
                        strSelectedName = frmSelectName.MyForm.SelectedValue;
                    }

                    if (dicCharacterSettings.Any(x => x.Value.Name == strSelectedName))
                    {
                        DialogResult eCreateDuplicateSetting = Program.ShowMessageBox(
                            string.Format(await LanguageManager.GetStringAsync("Message_CharacterOptions_DuplicateSettingName").ConfigureAwait(false),
                                strSelectedName),
                            await LanguageManager.GetStringAsync("MessageTitle_CharacterOptions_DuplicateFileName").ConfigureAwait(false),
                            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                        switch (eCreateDuplicateSetting)
                        {
                            case DialogResult.Cancel:
                                return;

                            case DialogResult.No:
                                strSelectedName = string.Empty;
                                break;
                        }
                    }
                } while (string.IsNullOrWhiteSpace(strSelectedName));

                string strBaseFileName = strSelectedName.CleanForFileName().TrimEndOnce(".xml");
                // Make sure our file name isn't too long, otherwise we run into problems on Windows
                // We can assume that Chummer's startup path plus 16 is within the limit, otherwise the user would have had problems installing Chummer with its data files in the first place
                int intStartupPathLimit = Utils.GetStartupPath.Length + 16;
                if (strBaseFileName.Length > intStartupPathLimit)
                    strBaseFileName = strBaseFileName.Substring(0, intStartupPathLimit);
                strSelectedFullFileName = strBaseFileName + ".xml";
                int intMaxNameLength = char.MaxValue - Utils.GetStartupPath.Length - "settings".Length - 6;
                uint uintAccumulator = 1;
                string strSeparator = "_";
                while (dicCharacterSettings.Any(x => x.Value.FileName == strSelectedFullFileName))
                {
                    strSelectedFullFileName = strBaseFileName + strSeparator + uintAccumulator.ToString(GlobalSettings.InvariantCultureInfo) + ".xml";
                    if (strSelectedFullFileName.Length > intMaxNameLength)
                    {
                        Program.ShowMessageBox(
                            await LanguageManager.GetStringAsync("Message_CharacterOptions_SettingFileNameTooLongError").ConfigureAwait(false),
                            await LanguageManager.GetStringAsync("MessageTitle_CharacterOptions_SettingFileNameTooLongError").ConfigureAwait(false),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        strSelectedName = string.Empty;
                        break;
                    }

                    if (uintAccumulator == uint.MaxValue)
                        uintAccumulator = uint.MinValue;
                    else if (++uintAccumulator == 1)
                        strSeparator += '_';
                }
            } while (string.IsNullOrWhiteSpace(strSelectedName));

            CursorWait objCursorWait = await CursorWait.NewAsync(this).ConfigureAwait(false);
            try
            {
                _objCharacterSettings.Name = strSelectedName;
                bool blnDoResumeLayout = !_blnIsLayoutSuspended;
                if (blnDoResumeLayout)
                {
                    _blnIsLayoutSuspended = true;
                    await this.DoThreadSafeAsync(x => x.SuspendLayout()).ConfigureAwait(false);
                }

                try
                {
                    CharacterSettings objNewCharacterSettings
                        = new CharacterSettings(_objCharacterSettings, false, strSelectedFullFileName);
                    if (!await dicCharacterSettings.TryAddAsync(
                            objNewCharacterSettings.DictionaryKey, objNewCharacterSettings).ConfigureAwait(false))
                    {
                        await objNewCharacterSettings.DisposeAsync().ConfigureAwait(false);
                        return;
                    }

                    if (!_objCharacterSettings.Save(strSelectedFullFileName, true))
                    {
                        // Revert addition of settings if we cannot create a file
                        await dicCharacterSettings.RemoveAsync(
                            objNewCharacterSettings.DictionaryKey).ConfigureAwait(false);
                        await objNewCharacterSettings.DisposeAsync().ConfigureAwait(false);
                        return;
                    }

                    // Force repopulate character settings list in Master Index from here in lieu of event handling for concurrent dictionaries
                    _blnForceMasterIndexRepopulateOnClose = true;
                    _objReferenceCharacterSettings = objNewCharacterSettings;
                    await SetIsDirty(false).ConfigureAwait(false);
                    await PopulateSettingsList().ConfigureAwait(false);
                }
                finally
                {
                    if (blnDoResumeLayout)
                    {
                        _blnIsLayoutSuspended = false;
                        await this.DoThreadSafeAsync(x => x.ResumeLayout()).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async void cmdSave_Click(object sender, EventArgs e)
        {
            CursorWait objCursorWait = await CursorWait.NewAsync(this).ConfigureAwait(false);
            try
            {
                if (_objReferenceCharacterSettings.BuildMethod != _objCharacterSettings.BuildMethod)
                {
                    using (new FetchSafelyFromPool<StringBuilder>(Utils.StringBuilderPool,
                                                                  out StringBuilder sbdConflictingCharacters))
                    {
                        foreach (Character objCharacter in Program.OpenCharacters)
                        {
                            if (!objCharacter.Created
                                && ReferenceEquals(objCharacter.Settings, _objReferenceCharacterSettings))
                                sbdConflictingCharacters.AppendLine(objCharacter.CharacterName);
                        }

                        if (sbdConflictingCharacters.Length > 0)
                        {
                            Program.ShowMessageBox(this,
                                                   await LanguageManager.GetStringAsync(
                                                       "Message_CharacterOptions_OpenCharacterOnBuildMethodChange").ConfigureAwait(false)
                                                   +
                                                   sbdConflictingCharacters,
                                                   await LanguageManager.GetStringAsync(
                                                       "MessageTitle_CharacterOptions_OpenCharacterOnBuildMethodChange").ConfigureAwait(false),
                                                   MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }
                }

                if (!_objCharacterSettings.Save())
                    return;
                bool blnDoResumeLayout = !_blnIsLayoutSuspended;
                if (blnDoResumeLayout)
                {
                    _blnIsLayoutSuspended = true;
                    await this.DoThreadSafeAsync(x => x.SuspendLayout()).ConfigureAwait(false);
                }

                try
                {
                    await _objReferenceCharacterSettings.CopyValuesAsync(_objCharacterSettings).ConfigureAwait(false);
                    await SetIsDirty(false).ConfigureAwait(false);
                }
                finally
                {
                    if (blnDoResumeLayout)
                    {
                        _blnIsLayoutSuspended = false;
                        await this.DoThreadSafeAsync(x => x.ResumeLayout()).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async void cboSetting_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_blnLoading)
                return;
            string strSelectedFile = await cboSetting.DoThreadSafeFuncAsync(x => x.SelectedValue?.ToString()).ConfigureAwait(false);
            if (string.IsNullOrEmpty(strSelectedFile))
                return;
            (bool blnSuccess, CharacterSettings objNewOption)
                = await (await SettingsManager.GetLoadedCharacterSettingsAsync().ConfigureAwait(false)).TryGetValueAsync(strSelectedFile).ConfigureAwait(false);
            if (!blnSuccess)
                return;

            if (IsDirty)
            {
                string text = await LanguageManager.GetStringAsync("Message_CharacterOptions_UnsavedDirty").ConfigureAwait(false);
                string caption = await LanguageManager.GetStringAsync("MessageTitle_CharacterOptions_UnsavedDirty").ConfigureAwait(false);

                if (Program.ShowMessageBox(text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) !=
                    DialogResult.Yes)
                {
                    _blnLoading = true;
                    try
                    {
                        await cboSetting.DoThreadSafeFuncAsync(x => x.SelectedIndex = _intOldSelectedSettingIndex).ConfigureAwait(false);
                    }
                    finally
                    {
                        _blnLoading = false;
                    }
                    return;
                }
                await SetIsDirty(false).ConfigureAwait(false);
            }

            CursorWait objCursorWait = await CursorWait.NewAsync(this).ConfigureAwait(false);
            try
            {
                _blnLoading = true;
                try
                {
                    bool blnDoResumeLayout = !_blnIsLayoutSuspended;
                    if (blnDoResumeLayout)
                    {
                        _blnIsLayoutSuspended = true;
                        await this.DoThreadSafeAsync(x => x.SuspendLayout()).ConfigureAwait(false);
                    }

                    try
                    {
                        if (_blnWasRenamed && _intOldSelectedSettingIndex >= 0)
                        {
                            int intCurrentSelectedSettingIndex
                                = await cboSetting.DoThreadSafeFuncAsync(x => x.SelectedIndex).ConfigureAwait(false);
                            ListItem objNewListItem =
                                new ListItem(_lstSettings[_intOldSelectedSettingIndex].Value,
                                             _objReferenceCharacterSettings.DisplayName);
                            _lstSettings[_intOldSelectedSettingIndex] = objNewListItem;
                            await cboSetting.PopulateWithListItemsAsync(_lstSettings).ConfigureAwait(false);
                            await cboSetting.DoThreadSafeAsync(x => x.SelectedIndex = intCurrentSelectedSettingIndex).ConfigureAwait(false);
                        }

                        _objReferenceCharacterSettings = objNewOption;
                        await _objCharacterSettings.CopyValuesAsync(objNewOption).ConfigureAwait(false);
                        await RebuildCustomDataDirectoryInfosAsync().ConfigureAwait(false);
                        await PopulateOptions().ConfigureAwait(false);
                        await SetIsDirty(false).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (blnDoResumeLayout)
                        {
                            _blnIsLayoutSuspended = false;
                            await this.DoThreadSafeAsync(x => x.ResumeLayout()).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    _blnLoading = false;
                }

                _intOldSelectedSettingIndex = cboSetting.SelectedIndex;
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async void cmdRestoreDefaults_Click(object sender, EventArgs e)
        {
            // Verify that the user wants to reset these values.
            if (Program.ShowMessageBox(
                await LanguageManager.GetStringAsync("Message_Options_RestoreDefaults").ConfigureAwait(false),
                await LanguageManager.GetStringAsync("MessageTitle_Options_RestoreDefaults").ConfigureAwait(false),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            CursorWait objCursorWait = await CursorWait.NewAsync(this).ConfigureAwait(false);
            try
            {
                _blnLoading = true;
                try
                {
                    bool blnDoResumeLayout = !_blnIsLayoutSuspended;
                    if (blnDoResumeLayout)
                    {
                        _blnIsLayoutSuspended = true;
                        await this.DoThreadSafeAsync(x => x.SuspendLayout()).ConfigureAwait(false);
                    }

                    try
                    {
                        int intCurrentSelectedSettingIndex
                            = await cboSetting.DoThreadSafeFuncAsync(x => x.SelectedIndex).ConfigureAwait(false);
                        if (_blnWasRenamed && intCurrentSelectedSettingIndex >= 0)
                        {
                            ListItem objNewListItem =
                                new ListItem(_lstSettings[intCurrentSelectedSettingIndex].Value,
                                             _objReferenceCharacterSettings.DisplayName);
                            _lstSettings[intCurrentSelectedSettingIndex] = objNewListItem;
                            await cboSetting.PopulateWithListItemsAsync(_lstSettings).ConfigureAwait(false);
                            await cboSetting.DoThreadSafeAsync(x => x.SelectedIndex = intCurrentSelectedSettingIndex).ConfigureAwait(false);
                        }

                        await _objCharacterSettings.CopyValuesAsync(_objReferenceCharacterSettings).ConfigureAwait(false);
                        await RebuildCustomDataDirectoryInfosAsync().ConfigureAwait(false);
                        await PopulateOptions().ConfigureAwait(false);
                        await SetIsDirty(false).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (blnDoResumeLayout)
                        {
                            _blnIsLayoutSuspended = false;
                            await this.DoThreadSafeAsync(x => x.ResumeLayout()).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    _blnLoading = false;
                }

                _intOldSelectedSettingIndex = cboSetting.SelectedIndex;
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void cboLimbCount_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_blnLoading || _blnSkipLimbCountUpdate)
                return;

            string strLimbCount = cboLimbCount.SelectedValue?.ToString();
            if (string.IsNullOrEmpty(strLimbCount))
            {
                _objCharacterSettings.LimbCount = 6;
                _objCharacterSettings.ExcludeLimbSlot = string.Empty;
            }
            else
            {
                int intSeparatorIndex = strLimbCount.IndexOf('<');
                if (intSeparatorIndex == -1)
                {
                    if (int.TryParse(strLimbCount, NumberStyles.Any, GlobalSettings.InvariantCultureInfo, out int intLimbCount))
                        _objCharacterSettings.LimbCount = intLimbCount;
                    else
                    {
                        Utils.BreakIfDebug();
                        _objCharacterSettings.LimbCount = 6;
                    }
                    _objCharacterSettings.ExcludeLimbSlot = string.Empty;
                }
                else
                {
                    if (int.TryParse(strLimbCount.Substring(0, intSeparatorIndex), NumberStyles.Any,
                        GlobalSettings.InvariantCultureInfo, out int intLimbCount))
                    {
                        _objCharacterSettings.LimbCount = intLimbCount;
                        _objCharacterSettings.ExcludeLimbSlot = intSeparatorIndex + 1 < strLimbCount.Length ? strLimbCount.Substring(intSeparatorIndex + 1) : string.Empty;
                    }
                    else
                    {
                        Utils.BreakIfDebug();
                        _objCharacterSettings.LimbCount = 6;
                        _objCharacterSettings.ExcludeLimbSlot = string.Empty;
                    }
                }
            }
        }

        private void cmdOK_Click(object sender, EventArgs e)
        {
            Close();
        }

        private async void EditCharacterSettings_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (IsDirty && Program.ShowMessageBox(await LanguageManager.GetStringAsync("Message_CharacterOptions_UnsavedDirty").ConfigureAwait(false),
                await LanguageManager.GetStringAsync("MessageTitle_CharacterOptions_UnsavedDirty").ConfigureAwait(false), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
            }

            if (_blnForceMasterIndexRepopulateOnClose && Program.MainForm.MasterIndex != null)
            {
                await Program.MainForm.MasterIndex.ForceRepopulateCharacterSettings().ConfigureAwait(false);
            }
        }

        private void cmdEnableSourcebooks_Click(object sender, EventArgs e)
        {
            _blnLoading = true;
            try
            {
                foreach (TreeNode objNode in treSourcebook.Nodes)
                {
                    string strBookCode = objNode.Tag.ToString();
                    if (!_setPermanentSourcebooks.Contains(strBookCode))
                    {
                        objNode.Checked = _blnSourcebookToggle;
                        if (_blnSourcebookToggle)
                            _objCharacterSettings.BooksWritable.Add(strBookCode);
                        else
                            _objCharacterSettings.BooksWritable.Remove(strBookCode);
                    }
                }
            }
            finally
            {
                _blnLoading = false;
            }
            _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.Books));
            _blnSourcebookToggle = !_blnSourcebookToggle;
        }

        private void treSourcebook_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_blnLoading)
                return;
            TreeNode objNode = e.Node;
            if (objNode == null)
                return;
            string strBookCode = objNode.Tag.ToString();
            if (string.IsNullOrEmpty(strBookCode) || (_setPermanentSourcebooks.Contains(strBookCode) && !objNode.Checked))
            {
                _blnLoading = true;
                try
                {
                    objNode.Checked = !objNode.Checked;
                }
                finally
                {
                    _blnLoading = false;
                }
                return;
            }
            if (objNode.Checked)
                _objCharacterSettings.BooksWritable.Add(strBookCode);
            else
                _objCharacterSettings.BooksWritable.Remove(strBookCode);
            _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.Books));
        }

        private async void cmdIncreaseCustomDirectoryLoadOrder_Click(object sender, EventArgs e)
        {
            TreeNode nodSelected = await treCustomDataDirectories.DoThreadSafeFuncAsync(x => x.SelectedNode).ConfigureAwait(false);
            if (nodSelected == null)
                return;
            int intIndex = nodSelected.Index;
            if (intIndex <= 0)
                return;
            _dicCharacterCustomDataDirectoryInfos.Reverse(intIndex - 1, 2);
            _objCharacterSettings.CustomDataDirectoryKeys.Reverse(intIndex - 1, 2);
            _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.CustomDataDirectoryKeys));
            await PopulateCustomDataDirectoryTreeView().ConfigureAwait(false);
        }

        private async void cmdToTopCustomDirectoryLoadOrder_Click(object sender, EventArgs e)
        {
            TreeNode nodSelected = await treCustomDataDirectories.DoThreadSafeFuncAsync(x => x.SelectedNode).ConfigureAwait(false);
            if (nodSelected == null)
                return;
            int intIndex = nodSelected.Index;
            if (intIndex <= 0)
                return;
            for (int i = intIndex; i > 0; --i)
            {
                _dicCharacterCustomDataDirectoryInfos.Reverse(i - 1, 2);
                _objCharacterSettings.CustomDataDirectoryKeys.Reverse(i - 1, 2);
            }
            _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.CustomDataDirectoryKeys));
            await PopulateCustomDataDirectoryTreeView().ConfigureAwait(false);
        }

        private async void cmdDecreaseCustomDirectoryLoadOrder_Click(object sender, EventArgs e)
        {
            TreeNode nodSelected = await treCustomDataDirectories.DoThreadSafeFuncAsync(x => x.SelectedNode).ConfigureAwait(false);
            if (nodSelected == null)
                return;
            int intIndex = nodSelected.Index;
            if (intIndex >= _dicCharacterCustomDataDirectoryInfos.Count - 1)
                return;
            _dicCharacterCustomDataDirectoryInfos.Reverse(intIndex, 2);
            _objCharacterSettings.CustomDataDirectoryKeys.Reverse(intIndex, 2);
            _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.CustomDataDirectoryKeys));
            await PopulateCustomDataDirectoryTreeView().ConfigureAwait(false);
        }

        private async void cmdToBottomCustomDirectoryLoadOrder_Click(object sender, EventArgs e)
        {
            TreeNode nodSelected = await treCustomDataDirectories.DoThreadSafeFuncAsync(x => x.SelectedNode).ConfigureAwait(false);
            if (nodSelected == null)
                return;
            int intIndex = nodSelected.Index;
            if (intIndex >= _dicCharacterCustomDataDirectoryInfos.Count - 1)
                return;
            for (int i = intIndex; i < _dicCharacterCustomDataDirectoryInfos.Count - 1; ++i)
            {
                _dicCharacterCustomDataDirectoryInfos.Reverse(i, 2);
                _objCharacterSettings.CustomDataDirectoryKeys.Reverse(i, 2);
            }
            _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.CustomDataDirectoryKeys));
            await PopulateCustomDataDirectoryTreeView().ConfigureAwait(false);
        }

        private void treCustomDataDirectories_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_blnLoading)
                return;
            TreeNode objNode = e.Node;
            if (objNode == null)
                return;
            int intIndex = objNode.Index;
            object objKey = _dicCharacterCustomDataDirectoryInfos[intIndex].Key;
            _dicCharacterCustomDataDirectoryInfos[objKey] = objNode.Checked;
            switch (objNode.Tag)
            {
                case CustomDataDirectoryInfo objCustomDataDirectoryInfo when _objCharacterSettings.CustomDataDirectoryKeys.ContainsKey(objCustomDataDirectoryInfo.CharacterSettingsSaveKey):
                    _objCharacterSettings.CustomDataDirectoryKeys[objCustomDataDirectoryInfo.CharacterSettingsSaveKey] = objNode.Checked;
                    _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.CustomDataDirectoryKeys));
                    break;
                case string strCustomDataDirectoryKey when _objCharacterSettings.CustomDataDirectoryKeys.ContainsKey(strCustomDataDirectoryKey):
                    _objCharacterSettings.CustomDataDirectoryKeys[strCustomDataDirectoryKey] = objNode.Checked;
                    _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.CustomDataDirectoryKeys));
                    break;
            }
        }

        private void txtPriorities_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = !char.IsControl(e.KeyChar)
                        && e.KeyChar != 'A' && e.KeyChar != 'B' && e.KeyChar != 'C' && e.KeyChar != 'D' && e.KeyChar != 'E'
                        && e.KeyChar != 'a' && e.KeyChar != 'b' && e.KeyChar != 'c' && e.KeyChar != 'd' && e.KeyChar != 'e';
            switch (e.KeyChar)
            {
                case 'a':
                    e.KeyChar = 'A';
                    break;

                case 'b':
                    e.KeyChar = 'B';
                    break;

                case 'c':
                    e.KeyChar = 'C';
                    break;

                case 'd':
                    e.KeyChar = 'D';
                    break;

                case 'e':
                    e.KeyChar = 'E';
                    break;
            }
        }

        private async void txtPriorities_TextChanged(object sender, EventArgs e)
        {
            Color objWindowTextColor = await ColorManager.GetWindowTextAsync().ConfigureAwait(false);
            await txtPriorities.DoThreadSafeAsync(x => x.ForeColor
                                                      = x.TextLength == 5
                                                          ? objWindowTextColor
                                                          : ColorManager.ErrorColor).ConfigureAwait(false);
        }

        private async void txtContactPoints_TextChanged(object sender, EventArgs e)
        {
            Color objColor
                = await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                    await txtContactPoints.DoThreadSafeFuncAsync(x => x.Text).ConfigureAwait(false)).ConfigureAwait(false)
                    ? await ColorManager.GetWindowTextAsync().ConfigureAwait(false)
                    : ColorManager.ErrorColor;
            await txtContactPoints.DoThreadSafeAsync(x => x.ForeColor = objColor).ConfigureAwait(false);
        }

        private async void txtKnowledgePoints_TextChanged(object sender, EventArgs e)
        {
            Color objColor
                = await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                    await txtKnowledgePoints.DoThreadSafeFuncAsync(x => x.Text).ConfigureAwait(false)).ConfigureAwait(false)
                    ? await ColorManager.GetWindowTextAsync().ConfigureAwait(false)
                    : ColorManager.ErrorColor;
            await txtKnowledgePoints.DoThreadSafeAsync(x => x.ForeColor = objColor).ConfigureAwait(false);
        }

        private async void txtNuyenExpression_TextChanged(object sender, EventArgs e)
        {
            if (_blnLoading)
                return;
            string strText = await txtNuyenExpression.DoThreadSafeFuncAsync(x => x.Text).ConfigureAwait(false);
            Color objColor
                = await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(strText.Replace("{Karma}", "1")
                    .Replace("{PriorityNuyen}", "1")).ConfigureAwait(false)
                    ? await ColorManager.GetWindowTextAsync().ConfigureAwait(false)
                    : ColorManager.ErrorColor;
            await txtNuyenExpression.DoThreadSafeAsync(x => x.ForeColor = objColor).ConfigureAwait(false);
            await _objCharacterSettings.SetChargenKarmaToNuyenExpressionAsync(strText).ConfigureAwait(false); // Not data-bound so that the setter can be asynchronous
        }

        private async void txtBoundSpiritLimit_TextChanged(object sender, EventArgs e)
        {
            Color objColor
                = await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                    await txtBoundSpiritLimit.DoThreadSafeFuncAsync(x => x.Text).ConfigureAwait(false)).ConfigureAwait(false)
                    ? await ColorManager.GetWindowTextAsync().ConfigureAwait(false)
                    : ColorManager.ErrorColor;
            await txtBoundSpiritLimit.DoThreadSafeAsync(x => x.ForeColor = objColor).ConfigureAwait(false);
        }

        private async void txtRegisteredSpriteLimit_TextChanged(object sender, EventArgs e)
        {
            Color objColor
                = await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                    await txtRegisteredSpriteLimit.DoThreadSafeFuncAsync(x => x.Text).ConfigureAwait(false)).ConfigureAwait(false)
                    ? await ColorManager.GetWindowTextAsync().ConfigureAwait(false)
                    : ColorManager.ErrorColor;
            await txtRegisteredSpriteLimit.DoThreadSafeAsync(x => x.ForeColor = objColor).ConfigureAwait(false);
        }

        private async void txtEssenceModifierPostExpression_TextChanged(object sender, EventArgs e)
        {
            string strText = await txtEssenceModifierPostExpression.DoThreadSafeFuncAsync(x => x.Text).ConfigureAwait(false);
            Color objColor
                = await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                    strText.Replace("{Modifier}", "1.0")).ConfigureAwait(false)
                    ? await ColorManager.GetWindowTextAsync().ConfigureAwait(false)
                    : ColorManager.ErrorColor;
            await txtEssenceModifierPostExpression.DoThreadSafeAsync(x => x.ForeColor = objColor).ConfigureAwait(false);
        }

        private async void txtLiftLimit_TextChanged(object sender, EventArgs e)
        {
            Color objColor
                = await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                    await txtLiftLimit.DoThreadSafeFuncAsync(x => x.Text).ConfigureAwait(false)).ConfigureAwait(false)
                    ? await ColorManager.GetWindowTextAsync().ConfigureAwait(false)
                    : ColorManager.ErrorColor;
            await txtLiftLimit.DoThreadSafeAsync(x => x.ForeColor = objColor).ConfigureAwait(false);
        }

        private async void txtCarryLimit_TextChanged(object sender, EventArgs e)
        {
            Color objColor
                = await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                    await txtCarryLimit.DoThreadSafeFuncAsync(x => x.Text).ConfigureAwait(false)).ConfigureAwait(false)
                    ? await ColorManager.GetWindowTextAsync().ConfigureAwait(false)
                    : ColorManager.ErrorColor;
            await txtCarryLimit.DoThreadSafeAsync(x => x.ForeColor = objColor).ConfigureAwait(false);
        }

        private async void txtEncumbranceInterval_TextChanged(object sender, EventArgs e)
        {
            Color objColor
                = await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                    await txtEncumbranceInterval.DoThreadSafeFuncAsync(x => x.Text).ConfigureAwait(false)).ConfigureAwait(false)
                    ? await ColorManager.GetWindowTextAsync().ConfigureAwait(false)
                    : ColorManager.ErrorColor;
            await txtEncumbranceInterval.DoThreadSafeAsync(x => x.ForeColor = objColor).ConfigureAwait(false);
        }

        private void chkGrade_CheckedChanged(object sender, EventArgs e)
        {
            if (!(sender is CheckBox chkGrade))
                return;

            string strGrade = chkGrade.Tag.ToString();
            if (chkGrade.Checked)
            {
                if (_objCharacterSettings.BannedWareGrades.Contains(strGrade))
                {
                    _objCharacterSettings.BannedWareGrades.Remove(strGrade);
                    _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.BannedWareGrades));
                }
            }
            else if (!_objCharacterSettings.BannedWareGrades.Contains(strGrade))
            {
                _objCharacterSettings.BannedWareGrades.Add(strGrade);
                _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.BannedWareGrades));
            }
        }

        private void cboPriorityTable_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_blnLoading)
                return;
            string strNewPriorityTable = cboPriorityTable.SelectedValue?.ToString();
            if (string.IsNullOrWhiteSpace(strNewPriorityTable))
                return;
            _objCharacterSettings.PriorityTable = strNewPriorityTable;
        }

        private void treCustomDataDirectories_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (!(e.Node?.Tag is CustomDataDirectoryInfo objSelected))
            {
                gpbDirectoryInfo.Visible = false;
                return;
            }

            gpbDirectoryInfo.SuspendLayout();
            try
            {
                rtbDirectoryDescription.Text = objSelected.DisplayDescription;
                lblDirectoryVersion.Text = objSelected.MyVersion.ToString();
                lblDirectoryAuthors.Text = objSelected.DisplayAuthors;
                lblDirectoryName.Text = objSelected.Name;

                if (objSelected.DependenciesList.Count > 0)
                {
                    using (new FetchSafelyFromPool<StringBuilder>(Utils.StringBuilderPool,
                                                                  out StringBuilder sbdDependencies))
                    {
                        foreach (DirectoryDependency dependency in objSelected.DependenciesList)
                            sbdDependencies.AppendLine(dependency.DisplayName);
                        lblDependencies.Text = sbdDependencies.ToString();
                    }
                }
                else
                {
                    //Make sure all old information is discarded
                    lblDependencies.Text = string.Empty;
                }

                if (objSelected.IncompatibilitiesList.Count > 0)
                {
                    using (new FetchSafelyFromPool<StringBuilder>(Utils.StringBuilderPool,
                                                                  out StringBuilder sbdIncompatibilities))
                    {
                        foreach (DirectoryDependency exclusivity in objSelected.IncompatibilitiesList)
                            sbdIncompatibilities.AppendLine(exclusivity.DisplayName);
                        lblIncompatibilities.Text = sbdIncompatibilities.ToString();
                    }
                }
                else
                {
                    //Make sure all old information is discarded
                    lblIncompatibilities.Text = string.Empty;
                }

                gpbDirectoryInfo.Visible = true;
            }
            finally
            {
                gpbDirectoryInfo.ResumeLayout();
            }
        }

        #endregion Control Events

        #region Methods

        private async ValueTask PopulateSourcebookTreeView(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            // Load the Sourcebook information.
            // Put the Sourcebooks into a List so they can first be sorted.
            object objOldSelected = await treSourcebook.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token).ConfigureAwait(false);
            await treSourcebook.DoThreadSafeAsync(x => x.BeginUpdate(), token).ConfigureAwait(false);
            try
            {
                await treSourcebook.DoThreadSafeAsync(x => x.Nodes.Clear(), token).ConfigureAwait(false);
                _setPermanentSourcebooks.Clear();
                foreach (XPathNavigator objXmlBook in await (await XmlManager.LoadXPathAsync(
                                                                "books.xml", _objCharacterSettings.EnabledCustomDataDirectoryPaths, token: token).ConfigureAwait(false))
                                                            .SelectAndCacheExpressionAsync("/chummer/books/book", token: token).ConfigureAwait(false))
                {
                    if (await objXmlBook.SelectSingleNodeAndCacheExpressionAsync("hide", token: token).ConfigureAwait(false) != null)
                        continue;
                    string strCode = (await objXmlBook.SelectSingleNodeAndCacheExpressionAsync("code", token: token).ConfigureAwait(false))?.Value;
                    if (string.IsNullOrEmpty(strCode))
                        continue;
                    bool blnChecked = _objCharacterSettings.Books.Contains(strCode);
                    if (await objXmlBook.SelectSingleNodeAndCacheExpressionAsync("permanent", token: token).ConfigureAwait(false) != null)
                    {
                        _setPermanentSourcebooks.Add(strCode);
                        if (_objCharacterSettings.BooksWritable.Add(strCode))
                            _objCharacterSettings.OnPropertyChanged(nameof(CharacterSettings.Books));
                        blnChecked = true;
                    }

                    string strTranslate = (await objXmlBook.SelectSingleNodeAndCacheExpressionAsync("translate", token: token).ConfigureAwait(false))?.Value;
                    string strName = (await objXmlBook.SelectSingleNodeAndCacheExpressionAsync("name", token: token).ConfigureAwait(false))?.Value;
                    await treSourcebook.DoThreadSafeAsync(x =>
                    {
                        TreeNode objNode = new TreeNode
                        {
                            Text = strTranslate ?? strName ?? string.Empty,
                            Tag = strCode,
                            Checked = blnChecked
                        };
                        x.Nodes.Add(objNode);
                    }, token).ConfigureAwait(false);
                }

                await treSourcebook.DoThreadSafeAsync(x =>
                {
                    x.Sort();
                    if (objOldSelected != null)
                        x.SelectedNode = x.FindNodeByTag(objOldSelected);
                }, token).ConfigureAwait(false);
            }
            finally
            {
                await treSourcebook.DoThreadSafeAsync(x => x.EndUpdate(), token).ConfigureAwait(false);
            }
        }

        private async ValueTask PopulateCustomDataDirectoryTreeView(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            object objOldSelected = await treCustomDataDirectories.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token).ConfigureAwait(false);
            await treCustomDataDirectories.DoThreadSafeAsync(x => x.BeginUpdate(), token).ConfigureAwait(false);
            try
            {
                string strFileNotFound = await LanguageManager.GetStringAsync("MessageTitle_FileNotFound", token: token).ConfigureAwait(false);
                Color objGrayTextColor = await ColorManager.GetGrayTextAsync(token).ConfigureAwait(false);
                if (_dicCharacterCustomDataDirectoryInfos.Count != treCustomDataDirectories.Nodes.Count)
                {
                    List<TreeNode> lstNodes = new List<TreeNode>(_dicCharacterCustomDataDirectoryInfos.Count);
                    foreach (KeyValuePair<object, bool> kvpInfo in _dicCharacterCustomDataDirectoryInfos)
                    {
                        token.ThrowIfCancellationRequested();
                        TreeNode objNode = new TreeNode
                        {
                            Tag = kvpInfo.Key,
                            Checked = kvpInfo.Value
                        };
                        if (kvpInfo.Key is CustomDataDirectoryInfo objInfo)
                        {
                            objNode.Text = objInfo.DisplayName;
                            if (objNode.Checked)
                            {
                                // check dependencies and exclusivities only if they could exist at all instead of calling and running into empty an foreach.
                                string missingDirectories = string.Empty;
                                if (objInfo.DependenciesList.Count > 0)
                                    missingDirectories = await objInfo.CheckDependencyAsync(_objCharacterSettings, token).ConfigureAwait(false);

                                string prohibitedDirectories = string.Empty;
                                if (objInfo.IncompatibilitiesList.Count > 0)
                                    prohibitedDirectories = await objInfo.CheckIncompatibilityAsync(_objCharacterSettings, token).ConfigureAwait(false);

                                if (!string.IsNullOrEmpty(missingDirectories)
                                    || !string.IsNullOrEmpty(prohibitedDirectories))
                                {
                                    objNode.ToolTipText
                                        = await CustomDataDirectoryInfo.BuildIncompatibilityDependencyStringAsync(
                                            missingDirectories, prohibitedDirectories, token).ConfigureAwait(false);
                                    objNode.ForeColor = ColorManager.ErrorColor;
                                }
                            }
                        }
                        else
                        {
                            objNode.Text = kvpInfo.Key.ToString();
                            objNode.ForeColor = objGrayTextColor;
                            objNode.ToolTipText = strFileNotFound;
                        }

                        lstNodes.Add(objNode);
                    }
                    await treCustomDataDirectories.DoThreadSafeAsync(x =>
                    {
                        x.Nodes.Clear();
                        foreach (TreeNode objNode in lstNodes)
                            x.Nodes.Add(objNode);
                    }, token).ConfigureAwait(false);
                }
                else
                {
                    Color objWindowTextColor = await ColorManager.GetWindowTextAsync(token).ConfigureAwait(false);
                    for (int i = 0; i < _dicCharacterCustomDataDirectoryInfos.Count; ++i)
                    {
                        KeyValuePair<object, bool> kvpInfo = _dicCharacterCustomDataDirectoryInfos[i];
                        int i1 = i;
                        TreeNode objNode = await treCustomDataDirectories.DoThreadSafeFuncAsync(x => x.Nodes[i1], token).ConfigureAwait(false);
                        await treCustomDataDirectories.DoThreadSafeAsync(() =>
                        {
                            objNode.Tag = kvpInfo.Key;
                            objNode.Checked = kvpInfo.Value;
                        }, token: token).ConfigureAwait(false);
                        if (kvpInfo.Key is CustomDataDirectoryInfo objInfo)
                        {
                            string strText = await objInfo.GetDisplayNameAsync(token).ConfigureAwait(false);
                            await treCustomDataDirectories.DoThreadSafeAsync(() => objNode.Text = strText, token).ConfigureAwait(false);
                            if (objNode.Checked)
                            {
                                // check dependencies and exclusivities only if they could exist at all instead of calling and running into empty an foreach.
                                string missingDirectories = string.Empty;
                                if (objInfo.DependenciesList.Count > 0)
                                    missingDirectories = await objInfo.CheckDependencyAsync(_objCharacterSettings, token).ConfigureAwait(false);

                                string prohibitedDirectories = string.Empty;
                                if (objInfo.IncompatibilitiesList.Count > 0)
                                    prohibitedDirectories = await objInfo.CheckIncompatibilityAsync(_objCharacterSettings, token).ConfigureAwait(false);

                                if (!string.IsNullOrEmpty(missingDirectories)
                                    || !string.IsNullOrEmpty(prohibitedDirectories))
                                {
                                    string strToolTip
                                        = await CustomDataDirectoryInfo.BuildIncompatibilityDependencyStringAsync(
                                            missingDirectories, prohibitedDirectories, token).ConfigureAwait(false);
                                    await treCustomDataDirectories.DoThreadSafeAsync(() =>
                                    {
                                        objNode.ToolTipText = strToolTip;
                                        objNode.ForeColor = ColorManager.ErrorColor;
                                    }, token: token).ConfigureAwait(false);
                                }
                                else
                                {
                                    await treCustomDataDirectories.DoThreadSafeAsync(() =>
                                    {
                                        objNode.ToolTipText = string.Empty;
                                        objNode.ForeColor = objWindowTextColor;
                                    }, token: token).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                await treCustomDataDirectories.DoThreadSafeAsync(() =>
                                {
                                    objNode.ToolTipText = string.Empty;
                                    objNode.ForeColor = objWindowTextColor;
                                }, token: token).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            await treCustomDataDirectories.DoThreadSafeAsync(() =>
                            {
                                objNode.Text = kvpInfo.Key.ToString();
                                objNode.ForeColor = objGrayTextColor;
                                objNode.ToolTipText = strFileNotFound;
                            }, token: token).ConfigureAwait(false);
                        }
                    }

                    if (objOldSelected != null)
                    {
                        await treCustomDataDirectories.DoThreadSafeAsync(x =>
                        {
                            x.SelectedNode = x.FindNodeByTag(objOldSelected);
                            x.ShowNodeToolTips = true;
                        }, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await treCustomDataDirectories.DoThreadSafeAsync(x => x.ShowNodeToolTips = true, token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await treCustomDataDirectories.DoThreadSafeAsync(x => x.EndUpdate(), token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Set the values for all of the controls based on the Options for the selected Setting.
        /// </summary>
        private async ValueTask PopulateOptions(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token).ConfigureAwait(false);
            try
            {
                bool blnDoResumeLayout = !_blnIsLayoutSuspended;
                if (blnDoResumeLayout)
                {
                    _blnIsLayoutSuspended = true;
                    await this.DoThreadSafeAsync(x => x.SuspendLayout(), token).ConfigureAwait(false);
                }

                try
                {
                    await PopulateSourcebookTreeView(token).ConfigureAwait(false);
                    await PopulatePriorityTableList(token).ConfigureAwait(false);
                    await PopulateLimbCountList(token).ConfigureAwait(false);
                    await PopulateAllowedGrades(token).ConfigureAwait(false);
                    await PopulateCustomDataDirectoryTreeView(token).ConfigureAwait(false);
                }
                finally
                {
                    if (blnDoResumeLayout)
                    {
                        _blnIsLayoutSuspended = false;
                        await this.DoThreadSafeAsync(x => x.ResumeLayout(), token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async ValueTask PopulatePriorityTableList(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token).ConfigureAwait(false);
            try
            {
                using (new FetchSafelyFromPool<List<ListItem>>(Utils.ListItemListPool,
                                                               out List<ListItem> lstPriorityTables))
                {
                    foreach (XPathNavigator objXmlNode in await (await XmlManager
                                                                       .LoadXPathAsync("priorities.xml",
                                                                           _objCharacterSettings.EnabledCustomDataDirectoryPaths,
                                                                           token: token).ConfigureAwait(false))
                                                                .SelectAndCacheExpressionAsync(
                                                                    "/chummer/prioritytables/prioritytable", token: token).ConfigureAwait(false))
                    {
                        string strName = objXmlNode.Value;
                        if (!string.IsNullOrEmpty(strName))
                            lstPriorityTables.Add(new ListItem(objXmlNode.Value,
                                                               (await objXmlNode
                                                                      .SelectSingleNodeAndCacheExpressionAsync(
                                                                          "@translate", token: token).ConfigureAwait(false))
                                                               ?.Value ?? strName));
                    }

                    string strOldSelected = _objCharacterSettings.PriorityTable;

                    bool blnOldLoading = _blnLoading;
                    _blnLoading = true;
                    await cboPriorityTable.PopulateWithListItemsAsync(lstPriorityTables, token).ConfigureAwait(false);
                    await cboPriorityTable.DoThreadSafeAsync(x =>
                    {
                        if (!string.IsNullOrEmpty(strOldSelected))
                            x.SelectedValue = strOldSelected;
                        if (x.SelectedIndex == -1 && lstPriorityTables.Count > 0)
                            x.SelectedValue = _objReferenceCharacterSettings.PriorityTable;
                        if (x.SelectedIndex == -1 && lstPriorityTables.Count > 0)
                            x.SelectedIndex = 0;
                    }, token).ConfigureAwait(false);
                    _blnLoading = blnOldLoading;
                }

                string strSelectedTable
                    = await cboPriorityTable.DoThreadSafeFuncAsync(x => x.SelectedValue?.ToString(), token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(strSelectedTable) &&
                    _objCharacterSettings.PriorityTable != strSelectedTable)
                    _objCharacterSettings.PriorityTable = strSelectedTable;
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async ValueTask PopulateLimbCountList(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token).ConfigureAwait(false);
            try
            {
                using (new FetchSafelyFromPool<List<ListItem>>(Utils.ListItemListPool,
                                                               out List<ListItem> lstLimbCount))
                {
                    foreach (XPathNavigator objXmlNode in await (await XmlManager
                                                                       .LoadXPathAsync("options.xml",
                                                                           _objCharacterSettings.EnabledCustomDataDirectoryPaths,
                                                                           token: token).ConfigureAwait(false))
                                                                .SelectAndCacheExpressionAsync("/chummer/limbcounts/limb", token: token).ConfigureAwait(false))
                    {
                        string strExclude = (await objXmlNode.SelectSingleNodeAndCacheExpressionAsync("exclude", token: token).ConfigureAwait(false))?.Value
                                            ??
                                            string.Empty;
                        if (!string.IsNullOrEmpty(strExclude))
                            strExclude = '<' + strExclude;
                        lstLimbCount.Add(new ListItem(
                                             (await objXmlNode.SelectSingleNodeAndCacheExpressionAsync("limbcount", token: token).ConfigureAwait(false))
                                             ?.Value + strExclude,
                                             (await objXmlNode.SelectSingleNodeAndCacheExpressionAsync("translate", token: token).ConfigureAwait(false))
                                             ?.Value
                                             ?? (await objXmlNode.SelectSingleNodeAndCacheExpressionAsync("name", token: token).ConfigureAwait(false))
                                             ?.Value
                                             ?? string.Empty));
                    }

                    string strLimbSlot = _objCharacterSettings.LimbCount.ToString(GlobalSettings.InvariantCultureInfo);
                    if (!string.IsNullOrEmpty(_objCharacterSettings.ExcludeLimbSlot))
                        strLimbSlot += '<' + _objCharacterSettings.ExcludeLimbSlot;

                    _blnSkipLimbCountUpdate = true;
                    await cboLimbCount.PopulateWithListItemsAsync(lstLimbCount, token).ConfigureAwait(false);
                    await cboLimbCount.DoThreadSafeAsync(x =>
                    {
                        if (!string.IsNullOrEmpty(strLimbSlot))
                            x.SelectedValue = strLimbSlot;
                        if (x.SelectedIndex == -1 && lstLimbCount.Count > 0)
                            x.SelectedIndex = 0;
                    }, token).ConfigureAwait(false);
                }

                _blnSkipLimbCountUpdate = false;
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async ValueTask PopulateAllowedGrades(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token).ConfigureAwait(false);
            try
            {
                using (new FetchSafelyFromPool<List<ListItem>>(Utils.ListItemListPool,
                                                               out List<ListItem> lstGrades))
                {
                    foreach (XPathNavigator objXmlNode in await (await XmlManager
                                                                       .LoadXPathAsync("bioware.xml",
                                                                           _objCharacterSettings.EnabledCustomDataDirectoryPaths,
                                                                           token: token).ConfigureAwait(false))
                                                                .SelectAndCacheExpressionAsync("/chummer/grades/grade[not(hide)]", token: token).ConfigureAwait(false))
                    {
                        string strName = (await objXmlNode.SelectSingleNodeAndCacheExpressionAsync("name", token: token).ConfigureAwait(false))?.Value;
                        if (!string.IsNullOrEmpty(strName) && strName != "None")
                        {
                            string strBook = (await objXmlNode.SelectSingleNodeAndCacheExpressionAsync("source", token: token).ConfigureAwait(false))
                                ?.Value;
                            if (!string.IsNullOrEmpty(strBook)
                                && treSourcebook.Nodes.Cast<TreeNode>().All(x => x.Tag.ToString() != strBook))
                                continue;
                            if (lstGrades.Any(x => strName.Contains(x.Value.ToString())))
                                continue;
                            ListItem objExistingCoveredGrade =
                                lstGrades.Find(x => x.Value.ToString().Contains(strName));
                            if (objExistingCoveredGrade.Value != null)
                                lstGrades.Remove(objExistingCoveredGrade);
                            lstGrades.Add(new ListItem(
                                              strName,
                                              (await objXmlNode.SelectSingleNodeAndCacheExpressionAsync("translate", token: token).ConfigureAwait(false))
                                              ?.Value
                                              ?? strName));
                        }
                    }

                    foreach (XPathNavigator objXmlNode in await (await XmlManager
                                                                       .LoadXPathAsync("cyberware.xml",
                                                                           _objCharacterSettings.EnabledCustomDataDirectoryPaths,
                                                                           token: token).ConfigureAwait(false))
                                                                .SelectAndCacheExpressionAsync("/chummer/grades/grade[not(hide)]", token: token).ConfigureAwait(false))
                    {
                        string strName = (await objXmlNode.SelectSingleNodeAndCacheExpressionAsync("name", token: token).ConfigureAwait(false))?.Value;
                        if (!string.IsNullOrEmpty(strName) && strName != "None")
                        {
                            string strBook = (await objXmlNode.SelectSingleNodeAndCacheExpressionAsync("source", token: token).ConfigureAwait(false))
                                ?.Value;
                            if (!string.IsNullOrEmpty(strBook)
                                && treSourcebook.Nodes.Cast<TreeNode>().All(x => x.Tag.ToString() != strBook))
                                continue;
                            if (lstGrades.Any(x => strName.Contains(x.Value.ToString())))
                                continue;
                            ListItem objExistingCoveredGrade =
                                lstGrades.Find(x => x.Value.ToString().Contains(strName));
                            if (objExistingCoveredGrade.Value != null)
                                lstGrades.Remove(objExistingCoveredGrade);
                            lstGrades.Add(new ListItem(
                                              strName,
                                              (await objXmlNode.SelectSingleNodeAndCacheExpressionAsync("translate", token: token).ConfigureAwait(false))
                                              ?.Value
                                              ?? strName));
                        }
                    }

                    await flpAllowedCyberwareGrades.DoThreadSafeAsync(x =>
                    {
                        x.SuspendLayout();
                        try
                        {
                            x.Controls.Clear();
                            foreach (ListItem objGrade in lstGrades)
                            {
                                ColorableCheckBox chkGrade = new ColorableCheckBox
                                {
                                    UseVisualStyleBackColor = true,
                                    Text = objGrade.Name,
                                    Tag = objGrade.Value,
                                    AutoSize = true,
                                    Anchor = AnchorStyles.Left,
                                    Checked = !_objCharacterSettings.BannedWareGrades.Contains(
                                        objGrade.Value.ToString())
                                };
                                chkGrade.CheckedChanged += chkGrade_CheckedChanged;
                                x.Controls.Add(chkGrade);
                            }
                        }
                        finally
                        {
                            x.ResumeLayout();
                        }
                    }, token).ConfigureAwait(false);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void RebuildCustomDataDirectoryInfos()
        {
            _dicCharacterCustomDataDirectoryInfos.Clear();
            foreach (KeyValuePair<string, bool> kvpCustomDataDirectory in _objCharacterSettings.CustomDataDirectoryKeys)
            {
                CustomDataDirectoryInfo objLoopInfo
                    = GlobalSettings.CustomDataDirectoryInfos.FirstOrDefault(
                        x => x.CharacterSettingsSaveKey == kvpCustomDataDirectory.Key);
                if (objLoopInfo != default)
                {
                    _dicCharacterCustomDataDirectoryInfos.Add(objLoopInfo, kvpCustomDataDirectory.Value);
                }
                else
                {
                    _dicCharacterCustomDataDirectoryInfos.Add(kvpCustomDataDirectory.Key,
                                                              kvpCustomDataDirectory.Value);
                }
            }
        }

        private async ValueTask RebuildCustomDataDirectoryInfosAsync(CancellationToken token = default)
        {
            _dicCharacterCustomDataDirectoryInfos.Clear();
            await _objCharacterSettings.CustomDataDirectoryKeys.ForEachAsync(
                kvpCustomDataDirectory =>
                {
                    CustomDataDirectoryInfo objLoopInfo
                        = GlobalSettings.CustomDataDirectoryInfos.FirstOrDefault(
                            x => x.CharacterSettingsSaveKey == kvpCustomDataDirectory.Key);
                    if (objLoopInfo != default)
                    {
                        _dicCharacterCustomDataDirectoryInfos.Add(objLoopInfo, kvpCustomDataDirectory.Value);
                    }
                    else
                    {
                        _dicCharacterCustomDataDirectoryInfos.Add(kvpCustomDataDirectory.Key,
                                                                  kvpCustomDataDirectory.Value);
                    }
                }, token: token).ConfigureAwait(false);
        }

        private async ValueTask SetToolTips(CancellationToken token = default)
        {
            await chkUnarmedSkillImprovements.SetToolTipAsync((await LanguageManager.GetStringAsync("Tip_OptionsUnarmedSkillImprovements", token: token).ConfigureAwait(false)).WordWrap(), token).ConfigureAwait(false);
            await chkIgnoreArt.SetToolTipAsync((await LanguageManager.GetStringAsync("Tip_OptionsIgnoreArt", token: token).ConfigureAwait(false)).WordWrap(), token).ConfigureAwait(false);
            await chkIgnoreComplexFormLimit.SetToolTipAsync((await LanguageManager.GetStringAsync("Tip_OptionsIgnoreComplexFormLimit", token: token).ConfigureAwait(false)).WordWrap(), token).ConfigureAwait(false);
            await chkCyberlegMovement.SetToolTipAsync((await LanguageManager.GetStringAsync("Tip_OptionsCyberlegMovement", token: token).ConfigureAwait(false)).WordWrap(), token).ConfigureAwait(false);
            await chkDontDoubleQualityPurchases.SetToolTipAsync((await LanguageManager.GetStringAsync("Tip_OptionsDontDoubleQualityPurchases", token: token).ConfigureAwait(false)).WordWrap(), token).ConfigureAwait(false);
            await chkDontDoubleQualityRefunds.SetToolTipAsync((await LanguageManager.GetStringAsync("Tip_OptionsDontDoubleQualityRefunds", token: token).ConfigureAwait(false)).WordWrap(), token).ConfigureAwait(false);
            await chkStrictSkillGroups.SetToolTipAsync((await LanguageManager.GetStringAsync("Tip_OptionStrictSkillGroups", token: token).ConfigureAwait(false)).WordWrap(), token).ConfigureAwait(false);
            await chkAllowInitiation.SetToolTipAsync((await LanguageManager.GetStringAsync("Tip_OptionsAllowInitiation", token: token).ConfigureAwait(false)).WordWrap(), token).ConfigureAwait(false);
            await chkUseCalculatedPublicAwareness.SetToolTipAsync((await LanguageManager.GetStringAsync("Tip_PublicAwareness", token: token).ConfigureAwait(false)).WordWrap(), token).ConfigureAwait(false);
        }

        private void SetupDataBindings()
        {
            cmdRename.DoOneWayNegatableDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.BuiltInOption));
            cmdDelete.DoOneWayNegatableDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.BuiltInOption));

            cboBuildMethod.DoDataBinding("SelectedValue", _objCharacterSettings, nameof(CharacterSettings.BuildMethod));
            lblPriorityTable.DoOneWayDataBinding("Visible", _objCharacterSettings, nameof(CharacterSettings.BuildMethodUsesPriorityTables));
            cboPriorityTable.DoOneWayDataBinding("Visible", _objCharacterSettings, nameof(CharacterSettings.BuildMethodUsesPriorityTables));
            lblPriorities.DoOneWayDataBinding("Visible", _objCharacterSettings, nameof(CharacterSettings.BuildMethodIsPriority));
            txtPriorities.DoOneWayDataBinding("Visible", _objCharacterSettings, nameof(CharacterSettings.BuildMethodIsPriority));
            txtPriorities.DoDataBinding("Text", _objCharacterSettings, nameof(CharacterSettings.PriorityArray));
            lblSumToTen.DoOneWayDataBinding("Visible", _objCharacterSettings, nameof(CharacterSettings.BuildMethodIsSumtoTen));
            nudSumToTen.DoOneWayDataBinding("Visible", _objCharacterSettings, nameof(CharacterSettings.BuildMethodIsSumtoTen));
            nudSumToTen.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.SumtoTen));
            nudStartingKarma.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.BuildKarma));
            nudMaxNuyenKarma.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.NuyenMaximumBP));
            nudMaxAvail.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaximumAvailability));
            nudQualityKarmaLimit.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.QualityKarmaLimit));
            nudMaxNumberMaxAttributes.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaxNumberMaxAttributesCreate));
            nudMaxSkillRatingCreate.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaxSkillRatingCreate));
            nudMaxKnowledgeSkillRatingCreate.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaxKnowledgeSkillRatingCreate));
            nudMaxSkillRatingCreate.DoDataBinding("Maximum", _objCharacterSettings, nameof(CharacterSettings.MaxSkillRating));
            nudMaxKnowledgeSkillRatingCreate.DoDataBinding("Maximum", _objCharacterSettings, nameof(CharacterSettings.MaxKnowledgeSkillRating));
            txtContactPoints.DoDataBinding("Text", _objCharacterSettings, nameof(CharacterSettings.ContactPointsExpression));
            txtKnowledgePoints.DoDataBinding("Text", _objCharacterSettings, nameof(CharacterSettings.KnowledgePointsExpression));
            txtRegisteredSpriteLimit.DoDataBinding("Text", _objCharacterSettings, nameof(CharacterSettings.RegisteredSpriteExpression));
            txtBoundSpiritLimit.DoDataBinding("Text", _objCharacterSettings, nameof(CharacterSettings.BoundSpiritExpression));
            txtEssenceModifierPostExpression.DoDataBinding("Text", _objCharacterSettings, nameof(CharacterSettings.EssenceModifierPostExpression));
            txtLiftLimit.DoDataBinding("Text", _objCharacterSettings, nameof(CharacterSettings.LiftLimitExpression));
            txtCarryLimit.DoDataBinding("Text", _objCharacterSettings, nameof(CharacterSettings.CarryLimitExpression));
            txtEncumbranceInterval.DoDataBinding("Text", _objCharacterSettings, nameof(CharacterSettings.EncumbranceIntervalExpression));
            nudWeightDecimals.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.WeightDecimals));

            chkEncumbrancePenaltyPhysicalLimit.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DoEncumbrancePenaltyPhysicalLimit));
            chkEncumbrancePenaltyMovementSpeed.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DoEncumbrancePenaltyMovementSpeed));
            chkEncumbrancePenaltyAgility.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DoEncumbrancePenaltyAgility));
            chkEncumbrancePenaltyReaction.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DoEncumbrancePenaltyReaction));
            chkEncumbrancePenaltyWoundModifier.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DoEncumbrancePenaltyWoundModifier));

            nudEncumbrancePenaltyPhysicalLimit.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.EncumbrancePenaltyPhysicalLimit));
            nudEncumbrancePenaltyMovementSpeed.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.EncumbrancePenaltyMovementSpeed));
            nudEncumbrancePenaltyAgility.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.EncumbrancePenaltyAgility));
            nudEncumbrancePenaltyReaction.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.EncumbrancePenaltyReaction));
            nudEncumbrancePenaltyWoundModifier.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.EncumbrancePenaltyWoundModifier));

            chkEnforceCapacity.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.EnforceCapacity));
            chkLicenseEachRestrictedItem.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.LicenseRestricted));
            chkReverseAttributePriorityOrder.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.ReverseAttributePriorityOrder));
            chkDronemods.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DroneMods));
            chkDronemodsMaximumPilot.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DroneModsMaximumPilot));
            chkRestrictRecoil.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.RestrictRecoil));
            chkStrictSkillGroups.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.StrictSkillGroupsInCreateMode));
            chkAllowPointBuySpecializationsOnKarmaSkills.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.AllowPointBuySpecializationsOnKarmaSkills));
            chkAllowFreeGrids.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.AllowFreeGrids));

            chkDontUseCyberlimbCalculation.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DontUseCyberlimbCalculation));
            chkCyberlegMovement.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.CyberlegMovement));
            chkCyberlimbAttributeBonusCap.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.CyberlimbAttributeBonusCapOverride));
            nudCyberlimbAttributeBonusCap.DoOneWayDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.CyberlimbAttributeBonusCapOverride));
            nudCyberlimbAttributeBonusCap.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.CyberlimbAttributeBonusCap));
            chkRedlinerLimbsSkull.DoNegatableDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.RedlinerExcludesSkull));
            chkRedlinerLimbsTorso.DoNegatableDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.RedlinerExcludesTorso));
            chkRedlinerLimbsArms.DoNegatableDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.RedlinerExcludesArms));
            chkRedlinerLimbsLegs.DoNegatableDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.RedlinerExcludesLegs));

            nudNuyenDecimalsMaximum.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaxNuyenDecimals));
            nudNuyenDecimalsMinimum.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MinNuyenDecimals));
            nudEssenceDecimals.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.EssenceDecimals));
            chkDontRoundEssenceInternally.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DontRoundEssenceInternally));

            nudMinInitiativeDice.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MinInitiativeDice));
            nudMaxInitiativeDice.DoDataBinding("Minimum", _objCharacterSettings, nameof(CharacterSettings.MinInitiativeDice));
            nudMaxInitiativeDice.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaxInitiativeDice));
            nudMinAstralInitiativeDice.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MinAstralInitiativeDice));
            nudMaxAstralInitiativeDice.DoDataBinding("Minimum", _objCharacterSettings, nameof(CharacterSettings.MinAstralInitiativeDice));
            nudMaxAstralInitiativeDice.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaxAstralInitiativeDice));
            nudMinColdSimInitiativeDice.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MinColdSimInitiativeDice));
            nudMaxColdSimInitiativeDice.DoDataBinding("Minimum", _objCharacterSettings, nameof(CharacterSettings.MinColdSimInitiativeDice));
            nudMaxColdSimInitiativeDice.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaxColdSimInitiativeDice));
            nudMinHotSimInitiativeDice.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MinHotSimInitiativeDice));
            nudMaxHotSimInitiativeDice.DoDataBinding("Minimum", _objCharacterSettings, nameof(CharacterSettings.MinHotSimInitiativeDice));
            nudMaxHotSimInitiativeDice.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaxHotSimInitiativeDice));

            chkEnable4eStyleEnemyTracking.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.EnableEnemyTracking));
            flpKarmaGainedFromEnemies.DoOneWayDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.EnableEnemyTracking));
            nudKarmaGainedFromEnemies.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaEnemy));
            chkEnemyKarmaQualityLimit.DoOneWayDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.EnableEnemyTracking));
            chkEnemyKarmaQualityLimit.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.EnemyKarmaQualityLimit));
            chkMoreLethalGameplay.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.MoreLethalGameplay));

            chkNoArmorEncumbrance.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.NoArmorEncumbrance));
            chkUncappedArmorAccessoryBonuses.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.UncappedArmorAccessoryBonuses));
            chkIgnoreArt.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.IgnoreArt));
            chkIgnoreComplexFormLimit.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.IgnoreComplexFormLimit));
            chkUnarmedSkillImprovements.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.UnarmedImprovementsApplyToWeapons));
            chkMysAdPp.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.MysAdeptAllowPpCareer));
            chkMysAdPp.DoOneWayNegatableDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.MysAdeptSecondMAGAttribute));
            chkPrioritySpellsAsAdeptPowers.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.PrioritySpellsAsAdeptPowers));
            chkPrioritySpellsAsAdeptPowers.DoOneWayNegatableDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.MysAdeptSecondMAGAttribute));
            chkMysAdeptSecondMAGAttribute.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.MysAdeptSecondMAGAttribute));
            chkMysAdeptSecondMAGAttribute.DoOneWayDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.MysAdeptSecondMAGAttributeEnabled));
            chkUsePointsOnBrokenGroups.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.UsePointsOnBrokenGroups));
            chkSpecialKarmaCost.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.SpecialKarmaCostBasedOnShownValue));
            chkUseCalculatedPublicAwareness.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.UseCalculatedPublicAwareness));
            chkAlternateMetatypeAttributeKarma.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.AlternateMetatypeAttributeKarma));
            chkCompensateSkillGroupKarmaDifference.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.CompensateSkillGroupKarmaDifference));
            chkFreeMartialArtSpecialization.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.FreeMartialArtSpecialization));
            chkIncreasedImprovedAbilityModifier.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.IncreasedImprovedAbilityMultiplier));
            chkAllowTechnomancerSchooling.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.AllowTechnomancerSchooling));
            chkAllowSkillRegrouping.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.AllowSkillRegrouping));
            chkSpecializationsBreakSkillGroups.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.SpecializationsBreakSkillGroups));
            chkDontDoubleQualityPurchases.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DontDoubleQualityPurchases));
            chkDontDoubleQualityRefunds.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DontDoubleQualityRefunds));
            chkDroneArmorMultiplier.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.DroneArmorMultiplierEnabled));
            nudDroneArmorMultiplier.DoOneWayDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.DroneArmorMultiplierEnabled));
            nudDroneArmorMultiplier.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.DroneArmorMultiplier));
            chkESSLossReducesMaximumOnly.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.ESSLossReducesMaximumOnly));
            chkExceedNegativeQualities.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.ExceedNegativeQualities));
            chkExceedNegativeQualitiesNoBonus.DoOneWayDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.ExceedNegativeQualities));
            chkExceedNegativeQualitiesNoBonus.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.ExceedNegativeQualitiesNoBonus));
            chkExceedPositiveQualities.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.ExceedPositiveQualities));
            chkExceedPositiveQualitiesCostDoubled.DoOneWayDataBinding("Enabled", _objCharacterSettings, nameof(CharacterSettings.ExceedPositiveQualities));
            chkExceedPositiveQualitiesCostDoubled.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.ExceedPositiveQualitiesCostDoubled));
            chkExtendAnyDetectionSpell.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.ExtendAnyDetectionSpell));
            chkAllowCyberwareESSDiscounts.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.AllowCyberwareESSDiscounts));
            chkAllowInitiation.DoDataBinding("Checked", _objCharacterSettings, nameof(CharacterSettings.AllowInitiationInCreateMode));
            nudMaxSkillRating.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaxSkillRating));
            nudMaxKnowledgeSkillRating.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MaxKnowledgeSkillRating));

            // Karma options.
            nudMetatypeCostsKarmaMultiplier.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.MetatypeCostsKarmaMultiplier));
            nudKarmaNuyenPerWftM.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.NuyenPerBPWftM));
            nudKarmaNuyenPerWftP.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.NuyenPerBPWftP));
            nudKarmaAttribute.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaAttribute));
            nudKarmaQuality.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaQuality));
            nudKarmaSpecialization.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaSpecialization));
            nudKarmaKnowledgeSpecialization.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaKnowledgeSpecialization));
            nudKarmaNewKnowledgeSkill.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaNewKnowledgeSkill));
            nudKarmaNewActiveSkill.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaNewActiveSkill));
            nudKarmaNewSkillGroup.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaNewSkillGroup));
            nudKarmaImproveKnowledgeSkill.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaImproveKnowledgeSkill));
            nudKarmaImproveActiveSkill.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaImproveActiveSkill));
            nudKarmaImproveSkillGroup.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaImproveSkillGroup));
            nudKarmaSpell.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaSpell));
            nudKarmaNewComplexForm.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaNewComplexForm));
            nudKarmaNewAIProgram.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaNewAIProgram));
            nudKarmaNewAIAdvancedProgram.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaNewAIAdvancedProgram));
            nudKarmaMetamagic.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaMetamagic));
            nudKarmaContact.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaContact));
            nudKarmaCarryover.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaCarryover));
            nudKarmaSpirit.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaSpirit));
            nudKarmaSpiritFettering.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaSpiritFettering));
            nudKarmaTechnique.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaTechnique));
            nudKarmaInitiation.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaInitiation));
            nudKarmaInitiationFlat.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaInitiationFlat));
            nudKarmaJoinGroup.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaJoinGroup));
            nudKarmaLeaveGroup.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaLeaveGroup));
            nudKarmaMysticAdeptPowerPoint.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaMysticAdeptPowerPoint));

            // Focus costs
            nudKarmaAlchemicalFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaAlchemicalFocus));
            nudKarmaBanishingFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaBanishingFocus));
            nudKarmaBindingFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaBindingFocus));
            nudKarmaCenteringFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaCenteringFocus));
            nudKarmaCounterspellingFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaCounterspellingFocus));
            nudKarmaDisenchantingFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaDisenchantingFocus));
            nudKarmaFlexibleSignatureFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaFlexibleSignatureFocus));
            nudKarmaMaskingFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaMaskingFocus));
            nudKarmaPowerFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaPowerFocus));
            nudKarmaQiFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaQiFocus));
            nudKarmaRitualSpellcastingFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaRitualSpellcastingFocus));
            nudKarmaSpellcastingFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaSpellcastingFocus));
            nudKarmaSpellShapingFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaSpellShapingFocus));
            nudKarmaSummoningFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaSummoningFocus));
            nudKarmaSustainingFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaSustainingFocus));
            nudKarmaWeaponFocus.DoDataBinding("Value", _objCharacterSettings, nameof(CharacterSettings.KarmaWeaponFocus));
        }

        private async ValueTask PopulateSettingsList(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token).ConfigureAwait(false);
            try
            {
                string strSelect = string.Empty;
                if (!_blnLoading)
                    strSelect = await cboSetting.DoThreadSafeFuncAsync(x => x.SelectedValue?.ToString(), token).ConfigureAwait(false);
                _lstSettings.Clear();
                foreach (KeyValuePair<string, CharacterSettings> kvpCharacterSettingsEntry in await SettingsManager.GetLoadedCharacterSettingsAsync(token).ConfigureAwait(false))
                {
                    _lstSettings.Add(new ListItem(kvpCharacterSettingsEntry.Key,
                                                  kvpCharacterSettingsEntry.Value.DisplayName));
                    if (ReferenceEquals(_objReferenceCharacterSettings, kvpCharacterSettingsEntry.Value))
                        strSelect = kvpCharacterSettingsEntry.Key;
                }

                _lstSettings.Sort(CompareListItems.CompareNames);
                await cboSetting.PopulateWithListItemsAsync(_lstSettings, token).ConfigureAwait(false);
                await cboSetting.DoThreadSafeAsync(x =>
                {
                    if (!string.IsNullOrEmpty(strSelect))
                        x.SelectedValue = strSelect;
                    if (x.SelectedIndex == -1 && _lstSettings.Count > 0)
                        x.SelectedValue = x.FindStringExact(GlobalSettings.DefaultCharacterSetting);
                    if (x.SelectedIndex == -1 && _lstSettings.Count > 0)
                        x.SelectedIndex = 0;
                }, token).ConfigureAwait(false);
                _intOldSelectedSettingIndex = await cboSetting.DoThreadSafeFuncAsync(x => x.SelectedIndex, token).ConfigureAwait(false);
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async void SettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            CursorWait objCursorWait = await CursorWait.NewAsync(this).ConfigureAwait(false);
            try
            {
                if (!_blnLoading)
                {
                    bool blnOldLoading = _blnLoading;
                    _blnLoading = true;
                    try
                    {
                        await SetIsDirty(!await _objCharacterSettings.HasIdenticalSettingsAsync(_objReferenceCharacterSettings).ConfigureAwait(false)).ConfigureAwait(false);
                        switch (e.PropertyName)
                        {
                            case nameof(CharacterSettings.EnabledCustomDataDirectoryPaths):
                                await PopulateOptions().ConfigureAwait(false);
                                break;

                            case nameof(CharacterSettings.PriorityTable):
                                await PopulatePriorityTableList().ConfigureAwait(false);
                                break;
                        }
                    }
                    finally
                    {
                        _blnLoading = blnOldLoading;
                    }
                }
                else
                {
                    switch (e.PropertyName)
                    {
                        case nameof(CharacterSettings.BuiltInOption):
                        {
                            bool blnAllTextBoxesLegal = await IsAllTextBoxesLegalAsync().ConfigureAwait(false);
                            await cmdSave.DoThreadSafeAsync(
                                x => x.Enabled = IsDirty && blnAllTextBoxesLegal
                                                         && !_objCharacterSettings.BuiltInOption).ConfigureAwait(false);
                            break;
                        }
                        case nameof(CharacterSettings.PriorityArray):
                        case nameof(CharacterSettings.BuildMethod):
                        {
                            bool blnAllTextBoxesLegal = await IsAllTextBoxesLegalAsync().ConfigureAwait(false);
                            await cmdSaveAs.DoThreadSafeAsync(x => x.Enabled = IsDirty && blnAllTextBoxesLegal).ConfigureAwait(false);
                            await cmdSave.DoThreadSafeAsync(
                                x => x.Enabled = IsDirty && blnAllTextBoxesLegal
                                                         && !_objCharacterSettings.BuiltInOption).ConfigureAwait(false);
                            break;
                        }
                        case nameof(CharacterSettings.ChargenKarmaToNuyenExpression): // Not data-bound so that the setter can be asynchronous
                        {
                            await txtNuyenExpression.DoThreadSafeAsync(
                                x => x.Text = _objCharacterSettings.ChargenKarmaToNuyenExpression).ConfigureAwait(false);
                            break;
                        }
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        private bool IsAllTextBoxesLegal()
        {
            if (_objCharacterSettings.BuildMethod == CharacterBuildMethod.Priority && _objCharacterSettings.PriorityArray.Length != 5)
                return false;

            return CommonFunctions.IsCharacterAttributeXPathValidOrNull(
                       _objCharacterSettings.ContactPointsExpression) &&
                   CommonFunctions.IsCharacterAttributeXPathValidOrNull(
                       _objCharacterSettings.KnowledgePointsExpression) &&
                   CommonFunctions.IsCharacterAttributeXPathValidOrNull(
                       _objCharacterSettings.ChargenKarmaToNuyenExpression.Replace("{Karma}", "1")
                                            .Replace("{PriorityNuyen}", "1")) &&
                   CommonFunctions.IsCharacterAttributeXPathValidOrNull(
                       _objCharacterSettings.EssenceModifierPostExpression.Replace("{Modifier}", "1.0")) &&
                   CommonFunctions.IsCharacterAttributeXPathValidOrNull(
                       _objCharacterSettings.RegisteredSpriteExpression) &&
                   CommonFunctions.IsCharacterAttributeXPathValidOrNull(
                       _objCharacterSettings.BoundSpiritExpression) &&
                   CommonFunctions.IsCharacterAttributeXPathValidOrNull(
                       _objCharacterSettings.LiftLimitExpression) &&
                   CommonFunctions.IsCharacterAttributeXPathValidOrNull(
                       _objCharacterSettings.CarryLimitExpression) &&
                   CommonFunctions.IsCharacterAttributeXPathValidOrNull(
                       _objCharacterSettings.EncumbranceIntervalExpression);
        }

        private async ValueTask<bool> IsAllTextBoxesLegalAsync(CancellationToken token = default)
        {
            if (_objCharacterSettings.BuildMethod == CharacterBuildMethod.Priority && _objCharacterSettings.PriorityArray.Length != 5)
                return false;

            return await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                       _objCharacterSettings.ContactPointsExpression, token: token).ConfigureAwait(false) &&
                   await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                       _objCharacterSettings.KnowledgePointsExpression, token: token).ConfigureAwait(false) &&
                   await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                       (await _objCharacterSettings.GetChargenKarmaToNuyenExpressionAsync(token).ConfigureAwait(false)).Replace("{Karma}", "1")
                       .Replace("{PriorityNuyen}", "1"), token: token).ConfigureAwait(false) &&
                   await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                       _objCharacterSettings.EssenceModifierPostExpression.Replace("{Modifier}", "1.0"), token:token).ConfigureAwait(false) &&
                   await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                       _objCharacterSettings.RegisteredSpriteExpression, token: token).ConfigureAwait(false) &&
                   await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                       _objCharacterSettings.BoundSpiritExpression, token: token).ConfigureAwait(false) &&
                   await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                       _objCharacterSettings.LiftLimitExpression, token: token).ConfigureAwait(false) &&
                   await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                       _objCharacterSettings.CarryLimitExpression, token: token).ConfigureAwait(false) &&
                   await CommonFunctions.IsCharacterAttributeXPathValidOrNullAsync(
                       _objCharacterSettings.EncumbranceIntervalExpression, token: token).ConfigureAwait(false);
        }

        private bool IsDirty => _blnDirty;

        private async ValueTask SetIsDirty(bool value, CancellationToken token = default)
        {
            if (_blnDirty == value)
                return;
            _blnDirty = value;
            string strText = await LanguageManager.GetStringAsync(value ? "String_Cancel" : "String_OK", token: token).ConfigureAwait(false);
            await cmdOK.DoThreadSafeAsync(x => x.Text = strText, token).ConfigureAwait(false);
            if (value)
            {
                bool blnIsAllTextBoxesLegal = await IsAllTextBoxesLegalAsync(token).ConfigureAwait(false);
                await cmdSaveAs.DoThreadSafeAsync(x => x.Enabled = blnIsAllTextBoxesLegal, token).ConfigureAwait(false);
                if (blnIsAllTextBoxesLegal)
                {
                    bool blnTemp = await _objCharacterSettings.GetBuiltInOptionAsync(token).ConfigureAwait(false);
                    await cmdSave.DoThreadSafeAsync(x => x.Enabled = !blnTemp, token).ConfigureAwait(false);
                }
                else
                    await cmdSave.DoThreadSafeAsync(x => x.Enabled = false, token).ConfigureAwait(false);
            }
            else
            {
                _blnWasRenamed = false;
                await cmdSaveAs.DoThreadSafeAsync(x => x.Enabled = false, token).ConfigureAwait(false);
                await cmdSave.DoThreadSafeAsync(x => x.Enabled = false, token).ConfigureAwait(false);
            }
        }

        #endregion Methods
    }
}
