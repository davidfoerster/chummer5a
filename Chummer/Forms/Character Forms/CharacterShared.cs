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
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.XPath;
using Chummer.Backend.Attributes;
using Chummer.Backend.Equipment;
using Chummer.UI.Attributes;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using NLog;
using OperationCanceledException = System.OperationCanceledException;

namespace Chummer
{
    /// <summary>
    /// Contains functionality shared between frmCreate and frmCareer
    /// </summary>
    [DesignerCategory("")]
    public class CharacterShared : Form, IHasCharacterObjects
    {
        private static readonly Lazy<Logger> s_ObjLogger = new Lazy<Logger>(LogManager.GetCurrentClassLogger);
        private static Logger Log => s_ObjLogger.Value;
        private static TelemetryClient TelemetryClient { get; } = new TelemetryClient();
        private readonly Character _objCharacter;
        private bool _blnIsDirty;
        private int _intRefreshingCount;
        private int _intLoadingCount = 1;
        private int _intUpdatingCount;
        private FileSystemWatcher _objCharacterFileWatcher;
        protected readonly SaveFileDialog dlgSaveFile;

        protected CancellationTokenSource GenericCancellationTokenSource { get; } = new CancellationTokenSource();

        protected CancellationToken GenericToken { get; }

        protected CharacterShared(Character objCharacter)
        {
            GenericToken = GenericCancellationTokenSource.Token;
            _objCharacter = objCharacter;
            CancellationTokenRegistration objCancellationRegistration
                = GenericToken.Register(() => _objUpdateCharacterInfoCancellationTokenSource?.Cancel(false));
            Disposed += (sender, args) => objCancellationRegistration.Dispose();
            _objCharacter.PropertyChanged += CharacterPropertyChanged;
            dlgSaveFile = new SaveFileDialog();
            Load += OnLoad;
            Program.MainForm.OpenCharacterEditorForms.Add(this);
            string name = "Show_Form_" + GetType();
            PageViewTelemetry pvt = new PageViewTelemetry(name)
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Timestamp = DateTimeOffset.UtcNow
            };
            pvt.Context.Operation.Name = "Operation CharacterShared.Constructor()";
            pvt.Properties.Add("Name", objCharacter?.Name);
            string strCharacterFileName = objCharacter?.FileName; // Store this in a local so that we avoid possible weird semaphore collisions in the Shown delegate
            pvt.Properties.Add("Path", strCharacterFileName);
            Shown += delegate
            {
                pvt.Duration = DateTimeOffset.UtcNow - pvt.Timestamp;
                if (strCharacterFileName != null && Uri.TryCreate(strCharacterFileName, UriKind.Absolute, out Uri uriResult))
                {
                    pvt.Url = uriResult;
                }
                TelemetryClient.TrackPageView(pvt);
            };
            if (GlobalSettings.LiveUpdateCleanCharacterFiles && !string.IsNullOrEmpty(strCharacterFileName) && File.Exists(strCharacterFileName))
            {
                _objCharacterFileWatcher = new FileSystemWatcher(Path.GetDirectoryName(strCharacterFileName) ?? Path.GetPathRoot(strCharacterFileName), Path.GetFileName(strCharacterFileName));
                _objCharacterFileWatcher.Changed += LiveUpdateFromCharacterFile;
            }
        }

        [Obsolete("This constructor is for use by form designers only.", true)]
        protected CharacterShared()
        {
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                dlgSaveFile.Filter = await LanguageManager.GetStringAsync("DialogFilter_Chum5", token: GenericToken) + '|' +
                                     await LanguageManager.GetStringAsync("DialogFilter_Chum5lz", token: GenericToken) + '|' +
                                     await LanguageManager.GetStringAsync("DialogFilter_All", token: GenericToken);
            }
            catch (OperationCanceledException)
            {
                //swallow this
            }
        }

        protected virtual void LiveUpdateFromCharacterFile(object sender, FileSystemEventArgs e)
        {
        }

        private async void CharacterPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(Character.Settings):
                {
                    _objCachedSettings = null;
                    try
                    {
                        await RequestCharacterUpdate(GenericToken);
                        await SetDirty(true, GenericToken);
                    }
                    catch (OperationCanceledException)
                    {
                        //swallow this
                    }
                    break;
                }
                case nameof(Character.FileName):
                {
                    FileSystemWatcher objNewWatcher = null;
                    if (GlobalSettings.LiveUpdateCleanCharacterFiles)
                    {
                        string strFileName = Path.GetFileName(CharacterObject.FileName);
                        if (!string.IsNullOrEmpty(strFileName))
                        {
                            objNewWatcher = new FileSystemWatcher(
                                Path.GetDirectoryName(CharacterObject.FileName)
                                ?? Path.GetPathRoot(CharacterObject.FileName), strFileName);
                            objNewWatcher.Changed += LiveUpdateFromCharacterFile;
                        }
                    }
                    Interlocked.Exchange(ref _objCharacterFileWatcher, objNewWatcher)?.Dispose();

                    break;
                }
            }
        }

        /// <summary>
        /// Set up data bindings to set Dirty flag and/or the flag to request a character update when specific collections change
        /// </summary>
        /// <param name="blnAddBindings"></param>
        protected void SetupCommonCollectionDatabindings(bool blnAddBindings)
        {
            if (blnAddBindings)
            {
                CharacterObject.Spells.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.ComplexForms.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Arts.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Enhancements.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Metamagics.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.InitiationGrades.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Powers.ListChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.AIPrograms.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.CritterPowers.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Qualities.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.MartialArts.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Lifestyles.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Contacts.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Spirits.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Armor.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.ArmorLocations.CollectionChanged += MakeDirty;
                CharacterObject.Weapons.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.WeaponLocations.CollectionChanged += MakeDirty;
                CharacterObject.Gear.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.GearLocations.CollectionChanged += MakeDirty;
                CharacterObject.Drugs.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Cyberware.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.Vehicles.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.VehicleLocations.CollectionChanged += MakeDirty;

                CharacterObject.Improvements.CollectionChanged += MakeDirtyWithCharacterUpdate;
                CharacterObject.ImprovementGroups.CollectionChanged += MakeDirty;
                CharacterObject.Calendar.ListChanged += MakeDirty;
                CharacterObject.SustainedCollection.CollectionChanged += MakeDirty;
                CharacterObject.ExpenseEntries.CollectionChanged += MakeDirtyWithCharacterUpdate;
            }
            else
            {
                CharacterObject.Spells.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.ComplexForms.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Arts.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Enhancements.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Metamagics.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.InitiationGrades.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Powers.ListChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.AIPrograms.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.CritterPowers.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Qualities.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.MartialArts.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Lifestyles.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Contacts.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Spirits.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Armor.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.ArmorLocations.CollectionChanged -= MakeDirty;
                CharacterObject.Weapons.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.WeaponLocations.CollectionChanged -= MakeDirty;
                CharacterObject.Gear.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.GearLocations.CollectionChanged -= MakeDirty;
                CharacterObject.Drugs.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Cyberware.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.Vehicles.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.VehicleLocations.CollectionChanged -= MakeDirty;

                CharacterObject.Improvements.CollectionChanged -= MakeDirtyWithCharacterUpdate;
                CharacterObject.ImprovementGroups.CollectionChanged -= MakeDirty;
                CharacterObject.Calendar.ListChanged -= MakeDirty;
                CharacterObject.SustainedCollection.CollectionChanged -= MakeDirty;
                CharacterObject.ExpenseEntries.CollectionChanged -= MakeDirtyWithCharacterUpdate;
            }
        }

        /// <summary>
        /// Wrapper for relocating contact forms.
        /// </summary>
        protected readonly struct TransportWrapper : IEquatable<TransportWrapper>
        {
            public Control Control { get; }

            public TransportWrapper(Control objControl)
            {
                Control = objControl;
            }

            public bool Equals(TransportWrapper other)
            {
                return Control.Equals(other.Control);
            }

            public override bool Equals(object obj)
            {
                return Control.Equals(obj);
            }

            public static bool operator ==(TransportWrapper objX, TransportWrapper objY)
            {
                return objX.Equals(objY);
            }

            public static bool operator !=(TransportWrapper objX, TransportWrapper objY)
            {
                return !objX.Equals(objY);
            }

            public static bool operator ==(TransportWrapper objX, object objY)
            {
                return objX.Equals(objY);
            }

            public static bool operator !=(TransportWrapper objX, object objY)
            {
                return !objX.Equals(objY);
            }

            public static bool operator ==(object objX, TransportWrapper objY)
            {
                return objX?.Equals(objY) ?? false;
            }

            public static bool operator !=(object objX, TransportWrapper objY)
            {
                return objX?.Equals(objY) ?? false;
            }

            public override int GetHashCode()
            {
                return Control.GetHashCode();
            }

            public override string ToString()
            {
                return Control.ToString();
            }
        }

        protected Stopwatch AutosaveStopWatch { get; } = Stopwatch.StartNew();

        /// <summary>
        /// Automatically Save the character to a backup folder.
        /// </summary>
        protected async Task AutoSaveCharacter(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            CursorWait objCursorWait = await CursorWait.NewAsync(this, true, token);
            try
            {
                try
                {
                    string strAutosavePath = Utils.GetAutosavesFolderPath;

                    if (!Directory.Exists(strAutosavePath))
                    {
                        try
                        {
                            Directory.CreateDirectory(strAutosavePath);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            Program.ShowMessageBox(
                                this, await LanguageManager.GetStringAsync("Message_Insufficient_Permissions_Warning", token: token));
                            return;
                        }
                    }

                    string strShowFileName = Path.GetFileName(CharacterObject.FileName);

                    if (string.IsNullOrEmpty(strShowFileName))
                    {
                        // Autosaves are always compressed
                        strShowFileName = CharacterObject.CharacterName.CleanForFileName() + ".chum5lz";
                    }

                    string strFilePath = Path.Combine(strAutosavePath, strShowFileName);
                    if (!await CharacterObject.SaveAsync(strFilePath, false, false, token))
                    {
                        Log.Info("Autosave failed for character " + CharacterObject.CharacterName + " ("
                                 + CharacterObject.FileName + ')');
                    }
                }
                finally
                {
                    AutosaveStopWatch.Restart();
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        /// <summary>
        /// Edit and update a Limit Modifier.
        /// </summary>
        /// <param name="treLimit"></param>
        /// <param name="token"></param>
        protected async ValueTask UpdateLimitModifier(TreeView treLimit, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (treLimit == null)
                return;
            TreeNode objSelectedNode = await treLimit.DoThreadSafeFuncAsync(x => x.SelectedNode, token);
            if (objSelectedNode == null || objSelectedNode.Level == 0)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strGuid = (objSelectedNode.Tag as IHasInternalId)?.InternalId ?? string.Empty;
                if (string.IsNullOrEmpty(strGuid) || strGuid.IsEmptyGuid())
                    return;
                LimitModifier objLimitModifier = CharacterObject.LimitModifiers.FindById(strGuid);
                //If the LimitModifier couldn't be found (Ie it comes from an Improvement or the user hasn't properly selected a treenode, fail out early.
                if (objLimitModifier == null)
                {
                    Program.ShowMessageBox(this, await LanguageManager.GetStringAsync("Warning_NoLimitFound", token: token));
                    return;
                }

                using (ThreadSafeForm<SelectLimitModifier> frmPickLimitModifier =
                       await ThreadSafeForm<SelectLimitModifier>.GetAsync(
                           () => new SelectLimitModifier(objLimitModifier, "Physical", "Mental", "Social"), token))
                {
                    if (await frmPickLimitModifier.ShowDialogSafeAsync(this, token) == DialogResult.Cancel)
                        return;

                    //Remove the old LimitModifier to ensure we don't double up.
                    await CharacterObject.LimitModifiers.RemoveAsync(objLimitModifier, token);
                    // Create the new limit modifier.
                    LimitModifier objNewLimitModifier = new LimitModifier(CharacterObject, strGuid);
                    objNewLimitModifier.Create(frmPickLimitModifier.MyForm.SelectedName,
                                               frmPickLimitModifier.MyForm.SelectedBonus,
                                               frmPickLimitModifier.MyForm.SelectedLimitType,
                                               frmPickLimitModifier.MyForm.SelectedCondition, true);

                    await CharacterObject.LimitModifiers.AddAsync(objNewLimitModifier, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        /// <summary>
        /// Edit the notes of an item tagged to a tree node
        /// </summary>
        /// <param name="treNode"></param>
        /// <param name="token"></param>
        protected async ValueTask WriteNotes(TreeNode treNode, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (!(treNode?.Tag is IHasNotes objNotes))
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                using (ThreadSafeForm<EditNotes> frmItemNotes =
                       await ThreadSafeForm<EditNotes>.GetAsync(
                           () => new EditNotes(objNotes.Notes, objNotes.NotesColor), token))
                {
                    if (await frmItemNotes.ShowDialogSafeAsync(this, token) != DialogResult.OK)
                        return;
                    objNotes.Notes = frmItemNotes.MyForm.Notes;
                    objNotes.NotesColor = frmItemNotes.MyForm.NotesColor;
                    await SetDirty(true, token);
                    TreeView objTreeView = treNode.TreeView;
                    if (objTreeView != null)
                    {
                        await objTreeView.DoThreadSafeAsync(() =>
                        {
                            treNode.ForeColor = objNotes.PreferredColor;
                            treNode.ToolTipText = objNotes.Notes.WordWrap();
                        }, token);
                    }
                    else
                    {
                        treNode.ForeColor = objNotes.PreferredColor;
                        treNode.ToolTipText = objNotes.Notes.WordWrap();
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        #region Refresh Treeviews and Panels

        protected async ValueTask RefreshAttributes(FlowLayoutPanel pnlAttributes, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, Label lblName = null, int intKarmaWidth = -1, int intValueWidth = -1, int intLimitsWidth = -1, CancellationToken token = default)
        {
            if (pnlAttributes == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await pnlAttributes.DoThreadSafeAsync(x =>
                    {
                        x.SuspendLayout();
                        try
                        {
                            x.Controls.Clear();
                            if (CharacterObject.AttributeSection.Attributes.Count > 0)
                            {
                                int intNameWidth = lblName?.PreferredWidth ?? 0;
                                Control[] aobjControls
                                    = new Control[CharacterObject.AttributeSection.Attributes.Count];
                                for (int i = 0; i < CharacterObject.AttributeSection.Attributes.Count; ++i)
                                {
                                    AttributeControl objControl =
                                        new AttributeControl(CharacterObject.AttributeSection.Attributes[i]);
                                    objControl.MinimumSize
                                        = new Size(x.ClientSize.Width, objControl.MinimumSize.Height);
                                    objControl.MaximumSize
                                        = new Size(x.ClientSize.Width, objControl.MaximumSize.Height);
                                    objControl.ValueChanged += MakeDirtyWithCharacterUpdate;
                                    intNameWidth = Math.Max(intNameWidth, objControl.NameWidth);
                                    aobjControls[i] = objControl;
                                }

                                if (lblName != null)
                                    lblName.MinimumSize = new Size(intNameWidth, lblName.MinimumSize.Height);
                                foreach (AttributeControl objControl in aobjControls.OfType<AttributeControl>())
                                    objControl.UpdateWidths(intNameWidth, intKarmaWidth, intValueWidth,
                                                            intLimitsWidth);
                                x.Controls.AddRange(aobjControls);
                            }
                        }
                        finally
                        {
                            x.ResumeLayout();
                        }
                    }, token);
                }
                else
                {
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            bool blnVaryingAddedWidths = false;
                            int intNewNameWidth = -1;
                            Control[] aobjControls = new Control[notifyCollectionChangedEventArgs.NewItems.Count];
                            await pnlAttributes.DoThreadSafeAsync(x =>
                            {
                                for (int i = 0; i < notifyCollectionChangedEventArgs.NewItems.Count; ++i)
                                {
                                    AttributeControl objControl =
                                        new AttributeControl(
                                            notifyCollectionChangedEventArgs.NewItems[i] as CharacterAttrib);
                                    objControl.MinimumSize = new Size(x.ClientSize.Width,
                                                                      objControl.MinimumSize.Height);
                                    objControl.MaximumSize = new Size(x.ClientSize.Width,
                                                                      objControl.MaximumSize.Height);
                                    objControl.ValueChanged += MakeDirtyWithCharacterUpdate;
                                    if (intNewNameWidth < 0)
                                        intNewNameWidth = objControl.NameWidth;
                                    else if (intNewNameWidth < objControl.NameWidth)
                                    {
                                        intNewNameWidth = objControl.NameWidth;
                                        blnVaryingAddedWidths = true;
                                    }

                                    aobjControls[i] = objControl;
                                }

                                int intOldNameWidth = lblName?.Width ??
                                                      (x.Controls.Count > 0
                                                          ? x.Controls[0].Width
                                                          : 0);
                                if (intNewNameWidth > intOldNameWidth)
                                {
                                    if (lblName != null)
                                        lblName.MinimumSize = new Size(intNewNameWidth, lblName.MinimumSize.Height);
                                    foreach (AttributeControl objControl in x.Controls)
                                        objControl.UpdateWidths(intNewNameWidth, intKarmaWidth, intValueWidth,
                                                                intLimitsWidth);
                                    if (blnVaryingAddedWidths)
                                        foreach (AttributeControl objControl in aobjControls.OfType<AttributeControl>())
                                            objControl.UpdateWidths(intNewNameWidth, intKarmaWidth, intValueWidth,
                                                                    intLimitsWidth);
                                }
                                else
                                {
                                    foreach (AttributeControl objControl in aobjControls.OfType<AttributeControl>())
                                        objControl.UpdateWidths(intOldNameWidth, intKarmaWidth, intValueWidth,
                                                                intLimitsWidth);
                                }

                                x.Controls.AddRange(aobjControls);
                            }, token);
                            break;
                        }

                        case NotifyCollectionChangedAction.Remove:
                        {
                            await pnlAttributes.DoThreadSafeAsync(x =>
                            {
                                foreach (CharacterAttrib objAttrib in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    foreach (AttributeControl objControl in x.Controls)
                                    {
                                        if (objControl.AttributeName == objAttrib.Abbrev)
                                        {
                                            objControl.ValueChanged -= MakeDirtyWithCharacterUpdate;
                                            x.Controls.Remove(objControl);
                                            objControl.Dispose();
                                        }
                                    }

                                    if (!CharacterObject.Created)
                                    {
                                        objAttrib.Base = 0;
                                        objAttrib.Karma = 0;
                                    }
                                }
                            }, token);
                            break;
                        }

                        case NotifyCollectionChangedAction.Replace:
                        {
                            await pnlAttributes.DoThreadSafeAsync(x =>
                            {
                                foreach (CharacterAttrib objAttrib in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    foreach (AttributeControl objControl in x.Controls)
                                    {
                                        if (objControl.AttributeName == objAttrib.Abbrev)
                                        {
                                            objControl.ValueChanged -= MakeDirtyWithCharacterUpdate;
                                            x.Controls.Remove(objControl);
                                            objControl.Dispose();
                                        }
                                    }

                                    if (!CharacterObject.Created)
                                    {
                                        objAttrib.Base = 0;
                                        objAttrib.Karma = 0;
                                    }
                                }
                            }, token);

                            bool blnVaryingAddedWidths = false;
                            int intNewNameWidth = -1;
                            Control[] aobjControls = new Control[notifyCollectionChangedEventArgs.NewItems.Count];
                            await pnlAttributes.DoThreadSafeAsync(x =>
                            {
                                for (int i = 0; i < notifyCollectionChangedEventArgs.NewItems.Count; ++i)
                                {
                                    AttributeControl objControl =
                                        new AttributeControl(
                                            notifyCollectionChangedEventArgs.NewItems[i] as CharacterAttrib);
                                    objControl.MinimumSize = new Size(x.ClientSize.Width,
                                                                      objControl.MinimumSize.Height);
                                    objControl.MaximumSize = new Size(x.ClientSize.Width,
                                                                      objControl.MaximumSize.Height);
                                    objControl.ValueChanged += MakeDirtyWithCharacterUpdate;
                                    if (intNewNameWidth < 0)
                                        intNewNameWidth = objControl.NameWidth;
                                    else if (intNewNameWidth < objControl.NameWidth)
                                    {
                                        intNewNameWidth = objControl.NameWidth;
                                        blnVaryingAddedWidths = true;
                                    }

                                    aobjControls[i] = objControl;
                                }

                                int intOldNameWidth = lblName?.Width ??
                                                      (x.Controls.Count > 0
                                                          ? x.Controls[0].Width
                                                          : 0);
                                if (intNewNameWidth > intOldNameWidth)
                                {
                                    if (lblName != null)
                                        lblName.MinimumSize = new Size(intNewNameWidth, lblName.MinimumSize.Height);
                                    foreach (AttributeControl objControl in x.Controls)
                                        objControl.UpdateWidths(intNewNameWidth, intKarmaWidth, intValueWidth,
                                                                intLimitsWidth);
                                    if (blnVaryingAddedWidths)
                                        foreach (AttributeControl objControl in aobjControls.OfType<AttributeControl>())
                                            objControl.UpdateWidths(intNewNameWidth, intKarmaWidth, intValueWidth,
                                                                    intLimitsWidth);
                                }
                                else
                                {
                                    foreach (AttributeControl objControl in aobjControls.OfType<AttributeControl>())
                                        objControl.UpdateWidths(intOldNameWidth, intKarmaWidth, intValueWidth,
                                                                intLimitsWidth);
                                }

                                x.Controls.AddRange(aobjControls);
                            }, token);
                            break;
                        }
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        /// <summary>
        /// Clears and updates the TreeView for Spells. Typically called as part of AddQuality or UpdateCharacterInfo.
        /// </summary>
        /// <param name="treSpells">Spells tree.</param>
        /// <param name="treMetamagic">Initiations tree.</param>
        /// <param name="cmsSpell">ContextMenuStrip that will be added to spells in the spell tree.</param>
        /// <param name="cmsInitiationNotes">ContextMenuStrip that will be added to spells in the initiations tree.</param>
        /// <param name="notifyCollectionChangedEventArgs">Arguments for the change to the underlying ObservableCollection.</param>
        /// <param name="token">Cancellation token to listen to.</param>
        protected async ValueTask RefreshSpells(TreeView treSpells, TreeView treMetamagic, ContextMenuStrip cmsSpell, ContextMenuStrip cmsInitiationNotes, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treSpells == null)
                return;
            TreeNode objCombatNode = null;
            TreeNode objDetectionNode = null;
            TreeNode objHealthNode = null;
            TreeNode objIllusionNode = null;
            TreeNode objManipulationNode = null;
            TreeNode objRitualsNode = null;
            TreeNode objEnchantmentsNode = null;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    string strSelectedId
                        = (await treSpells.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                            IHasInternalId)?.InternalId ?? string.Empty;
                    string strSelectedMetamagicId =
                        treMetamagic != null
                            ? (await treMetamagic.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                                IHasInternalId)?.InternalId ?? string.Empty
                            : string.Empty;

                    // Clear the default nodes of entries.
                    await treSpells.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                    // Add the Spells that exist.
                    foreach (Spell objSpell in CharacterObject.Spells)
                    {
                        if (objSpell.Grade > 0 && treMetamagic != null)
                        {
                            await treMetamagic.DoThreadSafeAsync(x => x.FindNodeByTag(objSpell)?.Remove(),
                                                                 token);
                        }

                        await AddToTree(objSpell, false);
                    }

                    await treSpells.DoThreadSafeAsync(x => x.SortCustomAlphabetically(strSelectedId), token);
                    if (treMetamagic != null)
                        await treMetamagic.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedMetamagicId),
                                                             token);
                }
                else
                {
                    await treSpells.DoThreadSafeAsync(x =>
                    {
                        objCombatNode = x.FindNode("Node_SelectedCombatSpells", false);
                        objDetectionNode = x.FindNode("Node_SelectedDetectionSpells", false);
                        objHealthNode = x.FindNode("Node_SelectedHealthSpells", false);
                        objIllusionNode = x.FindNode("Node_SelectedIllusionSpells", false);
                        objManipulationNode = x.FindNode("Node_SelectedManipulationSpells", false);
                        objRitualsNode = x.FindNode("Node_SelectedGeomancyRituals", false);
                        objEnchantmentsNode = x.FindNode("Node_SelectedEnchantments", false);
                    }, token);
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (Spell objSpell in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objSpell);
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (Spell objSpell in notifyCollectionChangedEventArgs.OldItems)
                            {
                                await treSpells.DoThreadSafeAsync(x =>
                                {
                                    TreeNode objNode = x.FindNodeByTag(objSpell);
                                    if (objNode != null)
                                    {
                                        TreeNode objParent = objNode.Parent;
                                        objNode.Remove();
                                        if (objParent.Level == 0 && objParent.Nodes.Count == 0)
                                            objParent.Remove();
                                    }
                                }, token);

                                if (objSpell.Grade > 0 && treMetamagic != null)
                                {
                                    await treMetamagic.DoThreadSafeAsync(
                                        x => x.FindNodeByTag(objSpell)?.Remove(), token);
                                }
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Replace:
                        {
                            List<TreeNode> lstOldParents =
                                new List<TreeNode>(notifyCollectionChangedEventArgs.OldItems.Count);
                            foreach (Spell objSpell in notifyCollectionChangedEventArgs.OldItems)
                            {
                                await treSpells.DoThreadSafeAsync(x =>
                                {
                                    TreeNode objNode = x.FindNodeByTag(objSpell);
                                    if (objNode != null)
                                    {
                                        lstOldParents.Add(objNode.Parent);
                                        objNode.Remove();
                                    }
                                }, token);

                                if (objSpell.Grade > 0 && treMetamagic != null)
                                {
                                    await treMetamagic.DoThreadSafeAsync(
                                        x => x.FindNodeByTag(objSpell)?.Remove(), token);
                                }
                            }

                            foreach (Spell objSpell in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objSpell);
                            }

                            await treSpells.DoThreadSafeAsync(() =>
                            {
                                foreach (TreeNode objOldParent in lstOldParents)
                                {
                                    if (objOldParent.Level == 0 && objOldParent.Nodes.Count == 0)
                                        objOldParent.Remove();
                                }
                            }, token);

                            break;
                        }
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }

            async ValueTask AddToTree(Spell objSpell, bool blnSingleAdd = true)
            {
                TreeNode objNode = objSpell.CreateTreeNode(cmsSpell);
                if (objNode == null)
                    return;
                TreeNode objParentNode = null;
                switch (objSpell.Category)
                {
                    case "Combat":
                        if (objCombatNode == null)
                        {
                            objCombatNode = new TreeNode
                            {
                                Tag = "Node_SelectedCombatSpells",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedCombatSpells", token: token)
                            };
                            await treSpells.DoThreadSafeAsync(x =>
                            {
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Insert(0, objCombatNode);
                                objCombatNode.Expand();
                            }, token);
                        }
                        objParentNode = objCombatNode;
                        break;

                    case "Detection":
                        if (objDetectionNode == null)
                        {
                            objDetectionNode = new TreeNode
                            {
                                Tag = "Node_SelectedDetectionSpells",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedDetectionSpells", token: token)
                            };
                            await treSpells.DoThreadSafeAsync(x =>
                            {
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Insert(objCombatNode == null ? 0 : 1, objDetectionNode);
                                objDetectionNode.Expand();
                            }, token);
                        }
                        objParentNode = objDetectionNode;
                        break;

                    case "Health":
                        if (objHealthNode == null)
                        {
                            objHealthNode = new TreeNode
                            {
                                Tag = "Node_SelectedHealthSpells",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedHealthSpells", token: token)
                            };
                            await treSpells.DoThreadSafeAsync(x =>
                            {
                                x.Nodes.Insert((objCombatNode == null ? 0 : 1) +
                                               // ReSharper disable once AssignNullToNotNullAttribute
                                               (objDetectionNode == null ? 0 : 1), objHealthNode);
                                objHealthNode.Expand();
                            }, token);
                        }
                        objParentNode = objHealthNode;
                        break;

                    case "Illusion":
                        if (objIllusionNode == null)
                        {
                            objIllusionNode = new TreeNode
                            {
                                Tag = "Node_SelectedIllusionSpells",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedIllusionSpells", token: token)
                            };
                            await treSpells.DoThreadSafeAsync(x =>
                            {
                                x.Nodes.Insert((objCombatNode == null ? 0 : 1) +
                                               (objDetectionNode == null ? 0 : 1) +
                                               // ReSharper disable once AssignNullToNotNullAttribute
                                               (objHealthNode == null ? 0 : 1), objIllusionNode);
                                objIllusionNode.Expand();
                            }, token);
                        }
                        objParentNode = objIllusionNode;
                        break;

                    case "Manipulation":
                        if (objManipulationNode == null)
                        {
                            objManipulationNode = new TreeNode
                            {
                                Tag = "Node_SelectedManipulationSpells",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedManipulationSpells", token: token)
                            };
                            await treSpells.DoThreadSafeAsync(x =>
                            {
                                x.Nodes.Insert((objCombatNode == null ? 0 : 1) +
                                               (objDetectionNode == null ? 0 : 1) +
                                               (objHealthNode == null ? 0 : 1) +
                                               // ReSharper disable once AssignNullToNotNullAttribute
                                               (objIllusionNode == null ? 0 : 1), objManipulationNode);
                                objManipulationNode.Expand();
                            }, token);
                        }
                        objParentNode = objManipulationNode;
                        break;

                    case "Rituals":
                        if (objRitualsNode == null)
                        {
                            objRitualsNode = new TreeNode
                            {
                                Tag = "Node_SelectedGeomancyRituals",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedGeomancyRituals", token: token)
                            };
                            await treSpells.DoThreadSafeAsync(x =>
                            {
                                x.Nodes.Insert((objCombatNode == null ? 0 : 1) +
                                               (objDetectionNode == null ? 0 : 1) +
                                               (objHealthNode == null ? 0 : 1) +
                                               (objIllusionNode == null ? 0 : 1) +
                                               // ReSharper disable once AssignNullToNotNullAttribute
                                               (objManipulationNode == null ? 0 : 1), objRitualsNode);
                                objRitualsNode.Expand();
                            }, token);
                        }
                        objParentNode = objRitualsNode;
                        break;

                    case "Enchantments":
                        if (objEnchantmentsNode == null)
                        {
                            objEnchantmentsNode = new TreeNode
                            {
                                Tag = "Node_SelectedEnchantments",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedEnchantments", token: token)
                            };
                            await treSpells.DoThreadSafeAsync(x =>
                            {
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Add(objEnchantmentsNode);
                                objEnchantmentsNode.Expand();
                            }, token);
                        }
                        objParentNode = objEnchantmentsNode;
                        break;
                }
                if (objSpell.Grade > 0)
                {
                    InitiationGrade objGrade = await CharacterObject.InitiationGrades.FirstOrDefaultAsync(x => x.Grade == objSpell.Grade, token);
                    if (objGrade != null && treMetamagic != null)
                    {
                        await treMetamagic.DoThreadSafeAsync(x =>
                        {
                            TreeNode nodMetamagicParent = x.FindNodeByTag(objGrade);
                            if (nodMetamagicParent != null)
                            {
                                TreeNodeCollection nodMetamagicParentChildren = nodMetamagicParent.Nodes;
                                TreeNode objMetamagicNode = objSpell.CreateTreeNode(cmsInitiationNotes, true);
                                int intNodesCount = nodMetamagicParentChildren.Count;
                                int intTargetIndex = 0;
                                for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                                {
                                    if (CompareTreeNodes.CompareText(nodMetamagicParentChildren[intTargetIndex],
                                                                     objMetamagicNode) >= 0)
                                    {
                                        break;
                                    }
                                }

                                nodMetamagicParentChildren.Insert(intTargetIndex, objMetamagicNode);
                                if (blnSingleAdd)
                                    x.SelectedNode = objMetamagicNode;
                            }
                        }, token);
                    }
                }

                if (objParentNode == null)
                    return;
                await treSpells.DoThreadSafeAsync(x =>
                {
                    if (blnSingleAdd)
                    {
                        TreeNodeCollection lstParentNodeChildren = objParentNode.Nodes;
                        int intNodesCount = lstParentNodeChildren.Count;
                        int intTargetIndex = 0;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }

                        lstParentNodeChildren.Insert(intTargetIndex, objNode);
                        x.SelectedNode = objNode;
                    }
                    else
                        objParentNode.Nodes.Add(objNode);
                }, token);
            }
        }

        protected async ValueTask RefreshAIPrograms(TreeView treAIPrograms, ContextMenuStrip cmsAdvancedProgram, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treAIPrograms == null)
                return;
            TreeNode objParentNode = null;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    string strSelectedId =
                        (await treAIPrograms.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                            IHasInternalId)?.InternalId ?? string.Empty;

                    await treAIPrograms.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                    // Add AI Programs.
                    foreach (AIProgram objAIProgram in CharacterObject.AIPrograms)
                    {
                        await AddToTree(objAIProgram, false);
                    }

                    await treAIPrograms.DoThreadSafeAsync(x => x.SortCustomAlphabetically(strSelectedId), token);
                }
                else
                {
                    objParentNode
                        = await treAIPrograms.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedAIPrograms", false),
                                                                    token);
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (AIProgram objAIProgram in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objAIProgram);
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Remove:
                        {
                            await treAIPrograms.DoThreadSafeAsync(x =>
                            {
                                foreach (AIProgram objAIProgram in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    TreeNode objNode = x.FindNodeByTag(objAIProgram);
                                    if (objNode != null)
                                    {
                                        TreeNode objParent = objNode.Parent;
                                        objNode.Remove();
                                        if (objParent.Level == 0 && objParent.Nodes.Count == 0)
                                            objParent.Remove();
                                    }
                                }
                            }, token);

                            break;
                        }
                        case NotifyCollectionChangedAction.Replace:
                        {
                            List<TreeNode> lstOldParents =
                                new List<TreeNode>(notifyCollectionChangedEventArgs.OldItems.Count);
                            await treAIPrograms.DoThreadSafeAsync(x =>
                            {
                                foreach (AIProgram objAIProgram in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    TreeNode objNode = x.FindNodeByTag(objAIProgram);
                                    if (objNode != null)
                                    {
                                        lstOldParents.Add(objNode.Parent);
                                        objNode.Remove();
                                    }
                                }
                            }, token);

                            foreach (AIProgram objAIProgram in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objAIProgram);
                            }

                            await treAIPrograms.DoThreadSafeAsync(() =>
                            {
                                foreach (TreeNode objOldParent in lstOldParents)
                                {
                                    if (objOldParent.Level == 0 && objOldParent.Nodes.Count == 0)
                                        objOldParent.Remove();
                                }
                            }, token);

                            break;
                        }
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }

            async ValueTask AddToTree(AIProgram objAIProgram, bool blnSingleAdd = true)
            {
                TreeNode objNode = objAIProgram.CreateTreeNode(cmsAdvancedProgram);
                if (objNode == null)
                    return;

                if (objParentNode == null)
                {
                    objParentNode = new TreeNode
                    {
                        Tag = "Node_SelectedAIPrograms",
                        Text = await LanguageManager.GetStringAsync("Node_SelectedAIPrograms", token: token)
                    };
                    await treAIPrograms.DoThreadSafeAsync(x =>
                    {
                        // ReSharper disable once AssignNullToNotNullAttribute
                        x.Nodes.Add(objParentNode);
                        objParentNode.Expand();
                    }, token);
                }

                await treAIPrograms.DoThreadSafeAsync(x =>
                {
                    if (objParentNode == null)
                        return;
                    if (blnSingleAdd)
                    {
                        TreeNodeCollection lstParentNodeChildren = objParentNode.Nodes;
                        int intNodesCount = lstParentNodeChildren.Count;
                        int intTargetIndex = 0;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }

                        lstParentNodeChildren.Insert(intTargetIndex, objNode);
                        x.SelectedNode = objNode;
                    }
                    else
                        objParentNode.Nodes.Add(objNode);
                }, token);
            }
        }

        protected async ValueTask RefreshComplexForms(TreeView treComplexForms, TreeView treMetamagic, ContextMenuStrip cmsComplexForm, ContextMenuStrip cmsInitiationNotes, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treComplexForms == null)
                return;
            TreeNode objParentNode = null;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    string strSelectedId =
                        (await treComplexForms.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                            IHasInternalId)?.InternalId ?? string.Empty;
                    string strSelectedMetamagicId =
                        treMetamagic != null
                            ? (await treMetamagic.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                                IHasInternalId)?.InternalId ?? string.Empty
                            : string.Empty;

                    await treComplexForms.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                    // Add Complex Forms.
                    foreach (ComplexForm objComplexForm in CharacterObject.ComplexForms)
                    {
                        if (objComplexForm.Grade > 0 && treMetamagic != null)
                        {
                            await treMetamagic.DoThreadSafeAsync(x => x.FindNodeByTag(objComplexForm)?.Remove(),
                                                                 token);
                        }

                        await AddToTree(objComplexForm, false);
                    }

                    await treComplexForms.DoThreadSafeAsync(x => x.SortCustomAlphabetically(strSelectedId),
                                                            token);
                    if (treMetamagic != null)
                        await treMetamagic.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedMetamagicId),
                                                             token);
                }
                else
                {
                    objParentNode
                        = await treComplexForms.DoThreadSafeFuncAsync(
                            x => x.FindNode("Node_SelectedAdvancedComplexForms", false), token);
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (ComplexForm objComplexForm in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objComplexForm);
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (ComplexForm objComplexForm in notifyCollectionChangedEventArgs.OldItems)
                            {
                                await treComplexForms.DoThreadSafeAsync(x =>
                                {
                                    TreeNode objNode = x.FindNodeByTag(objComplexForm);
                                    if (objNode != null)
                                    {
                                        TreeNode objParent = objNode.Parent;
                                        objNode.Remove();
                                        if (objParent.Level == 0 && objParent.Nodes.Count == 0)
                                            objParent.Remove();
                                    }
                                }, token);

                                if (objComplexForm.Grade > 0 && treMetamagic != null)
                                {
                                    await treMetamagic.DoThreadSafeAsync(
                                        x => x.FindNodeByTag(objComplexForm)?.Remove(), token);
                                }
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Replace:
                        {
                            List<TreeNode> lstOldParents =
                                new List<TreeNode>(notifyCollectionChangedEventArgs.OldItems.Count);
                            foreach (ComplexForm objComplexForm in notifyCollectionChangedEventArgs.OldItems)
                            {
                                await treComplexForms.DoThreadSafeAsync(x =>
                                {
                                    TreeNode objNode = x.FindNodeByTag(objComplexForm);
                                    if (objNode != null)
                                    {
                                        lstOldParents.Add(objNode.Parent);
                                        objNode.Remove();
                                    }
                                }, token);

                                if (objComplexForm.Grade > 0 && treMetamagic != null)
                                {
                                    await treMetamagic.DoThreadSafeAsync(
                                        x => x.FindNodeByTag(objComplexForm)?.Remove(), token);
                                }
                            }

                            foreach (ComplexForm objComplexForm in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objComplexForm);
                            }

                            await treComplexForms.DoThreadSafeAsync(() =>
                            {
                                foreach (TreeNode objOldParent in lstOldParents)
                                {
                                    if (objOldParent.Level == 0 && objOldParent.Nodes.Count == 0)
                                        objOldParent.Remove();
                                }
                            }, token);

                            break;
                        }
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }

            async ValueTask AddToTree(ComplexForm objComplexForm, bool blnSingleAdd = true)
            {
                TreeNode objNode = objComplexForm.CreateTreeNode(cmsComplexForm);
                if (objNode == null)
                    return;
                if (objParentNode == null)
                {
                    objParentNode = new TreeNode
                    {
                        Tag = "Node_SelectedAdvancedComplexForms",
                        Text = await LanguageManager.GetStringAsync("Node_SelectedAdvancedComplexForms", token: token)
                    };
                    await treComplexForms.DoThreadSafeAsync(x =>
                    {
                        // ReSharper disable once AssignNullToNotNullAttribute
                        x.Nodes.Add(objParentNode);
                        objParentNode.Expand();
                    }, token);
                }
                if (objComplexForm.Grade > 0)
                {
                    InitiationGrade objGrade = await CharacterObject.InitiationGrades.FirstOrDefaultAsync(x => x.Grade == objComplexForm.Grade, token);
                    if (objGrade != null && treMetamagic != null)
                    {
                        await treMetamagic.DoThreadSafeAsync(x =>
                        {
                            TreeNode nodMetamagicParent = x.FindNodeByTag(objGrade);
                            if (nodMetamagicParent != null)
                            {
                                TreeNodeCollection nodMetamagicParentChildren = nodMetamagicParent.Nodes;
                                TreeNode objMetamagicNode = objComplexForm.CreateTreeNode(cmsInitiationNotes);
                                int intNodesCount = nodMetamagicParentChildren.Count;
                                int intTargetIndex = 0;
                                for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                                {
                                    if (CompareTreeNodes.CompareText(nodMetamagicParentChildren[intTargetIndex],
                                                                     objMetamagicNode) >= 0)
                                    {
                                        break;
                                    }
                                }

                                nodMetamagicParentChildren.Insert(intTargetIndex, objMetamagicNode);
                                if (blnSingleAdd)
                                    x.SelectedNode = objMetamagicNode;
                            }
                        }, token);
                    }
                }

                await treComplexForms.DoThreadSafeAsync(x =>
                {
                    if (objParentNode == null)
                        return;
                    if (blnSingleAdd)
                    {
                        TreeNodeCollection lstParentNodeChildren = objParentNode.Nodes;
                        int intNodesCount = lstParentNodeChildren.Count;
                        int intTargetIndex = 0;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }

                        lstParentNodeChildren.Insert(intTargetIndex, objNode);
                        x.SelectedNode = objNode;
                    }
                    else
                        objParentNode.Nodes.Add(objNode);
                }, token);
            }
        }

        protected async ValueTask RefreshInitiationGrades(TreeView treMetamagic, ContextMenuStrip cmsMetamagic, ContextMenuStrip cmsInitiationNotes, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treMetamagic == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    (string strSelectedId, TreeNodeCollection lstRootNodes) = await treMetamagic.DoThreadSafeFuncAsync(
                        x =>
                        {
                            string strReturn =
                                (x.SelectedNode?.Tag as IHasInternalId)?.InternalId ?? string.Empty;
                            TreeNodeCollection lstReturn = x.Nodes;
                            lstReturn.Clear();
                            return new Tuple<string, TreeNodeCollection>(strReturn, lstReturn);
                        }, token);

                    foreach (InitiationGrade objGrade in CharacterObject.InitiationGrades)
                    {
                        await AddToTree(objGrade);
                    }

                    await treMetamagic.DoThreadSafeAsync(x =>
                    {
                        int intOffset = lstRootNodes.Count;
                        foreach (Metamagic objMetamagic in CharacterObject.Metamagics)
                        {
                            if (objMetamagic.Grade < 0)
                            {
                                TreeNode objNode = objMetamagic.CreateTreeNode(cmsInitiationNotes, true);
                                if (objNode != null)
                                {
                                    int intNodesCount = lstRootNodes.Count;
                                    int intTargetIndex = intOffset;
                                    for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                                    {
                                        if (CompareTreeNodes.CompareText(lstRootNodes[intTargetIndex], objNode) >= 0)
                                        {
                                            break;
                                        }
                                    }

                                    lstRootNodes.Insert(intTargetIndex, objNode);
                                    objNode.Expand();
                                }
                            }
                        }

                        x.SelectedNode = x.FindNode(strSelectedId);
                    }, token);
                }
                else
                {
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (InitiationGrade objGrade in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objGrade, intNewIndex);
                                ++intNewIndex;
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Remove:
                        {
                            await treMetamagic.DoThreadSafeAsync(x =>
                            {
                                foreach (InitiationGrade objGrade in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    x.FindNodeByTag(objGrade)?.Remove();
                                }
                            }, token);
                        }
                            break;

                        case NotifyCollectionChangedAction.Replace:
                        {
                            await treMetamagic.DoThreadSafeAsync(x =>
                            {
                                foreach (InitiationGrade objGrade in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    x.FindNodeByTag(objGrade)?.Remove();
                                }
                            }, token);

                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (InitiationGrade objGrade in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objGrade, intNewIndex);
                                ++intNewIndex;
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Move:
                        {
                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            await treMetamagic.DoThreadSafeAsync(x =>
                            {
                                foreach (InitiationGrade objGrade in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    TreeNode nodGrade = x.FindNodeByTag(objGrade);
                                    if (nodGrade != null)
                                    {
                                        nodGrade.Remove();
                                        x.Nodes.Insert(intNewIndex, nodGrade);
                                        ++intNewIndex;
                                    }
                                }
                            }, token);
                        }
                            break;
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }

            Task AddToTree(InitiationGrade objInitiationGrade, int intIndex = -1)
            {
                TreeNode nodGrade = objInitiationGrade.CreateTreeNode(cmsMetamagic);
                TreeNodeCollection lstParentNodeChildren = nodGrade.Nodes;
                foreach (Art objArt in CharacterObject.Arts)
                {
                    if (objArt.Grade == objInitiationGrade.Grade)
                    {
                        TreeNode objNode = objArt.CreateTreeNode(cmsInitiationNotes, true);
                        if (objNode == null)
                            continue;
                        int intNodesCount = lstParentNodeChildren.Count;
                        int intTargetIndex = 0;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }
                        lstParentNodeChildren.Insert(intTargetIndex, objNode);
                    }
                }
                foreach (Metamagic objMetamagic in CharacterObject.Metamagics)
                {
                    if (objMetamagic.Grade == objInitiationGrade.Grade)
                    {
                        TreeNode objNode = objMetamagic.CreateTreeNode(cmsInitiationNotes, true);
                        if (objNode == null)
                            continue;
                        int intNodesCount = lstParentNodeChildren.Count;
                        int intTargetIndex = 0;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }
                        lstParentNodeChildren.Insert(intTargetIndex, objNode);
                    }
                }
                foreach (Spell objSpell in CharacterObject.Spells)
                {
                    if (objSpell.Grade == objInitiationGrade.Grade)
                    {
                        TreeNode objNode = objSpell.CreateTreeNode(cmsInitiationNotes, true);
                        if (objNode == null)
                            continue;
                        int intNodesCount = lstParentNodeChildren.Count;
                        int intTargetIndex = 0;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }
                        lstParentNodeChildren.Insert(intTargetIndex, objNode);
                    }
                }
                foreach (ComplexForm objComplexForm in CharacterObject.ComplexForms)
                {
                    if (objComplexForm.Grade == objInitiationGrade.Grade)
                    {
                        TreeNode objNode = objComplexForm.CreateTreeNode(cmsInitiationNotes);
                        if (objNode == null)
                            continue;
                        int intNodesCount = lstParentNodeChildren.Count;
                        int intTargetIndex = 0;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }
                        lstParentNodeChildren.Insert(intTargetIndex, objNode);
                    }
                }
                foreach (Enhancement objEnhancement in CharacterObject.Enhancements)
                {
                    if (objEnhancement.Grade == objInitiationGrade.Grade)
                    {
                        TreeNode objNode = objEnhancement.CreateTreeNode(cmsInitiationNotes, true);
                        if (objNode == null)
                            continue;
                        int intNodesCount = lstParentNodeChildren.Count;
                        int intTargetIndex = 0;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }
                        lstParentNodeChildren.Insert(intTargetIndex, objNode);
                    }
                }
                foreach (Power objPower in CharacterObject.Powers)
                {
                    foreach (Enhancement objEnhancement in objPower.Enhancements)
                    {
                        if (objEnhancement.Grade == objInitiationGrade.Grade)
                        {
                            TreeNode objNode = objEnhancement.CreateTreeNode(cmsInitiationNotes, true);
                            if (objNode == null)
                                continue;
                            int intNodesCount = lstParentNodeChildren.Count;
                            int intTargetIndex = 0;
                            for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                            {
                                if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                                {
                                    break;
                                }
                            }
                            lstParentNodeChildren.Insert(intTargetIndex, objNode);
                        }
                    }
                }
                nodGrade.Expand();
                return treMetamagic.DoThreadSafeAsync(x =>
                {
                    if (intIndex < 0)
                        x.Nodes.Add(nodGrade);
                    else
                        x.Nodes.Insert(intIndex, nodGrade);
                }, token);
            }
        }

        protected async ValueTask RefreshArtCollection(TreeView treMetamagic, ContextMenuStrip cmsMetamagic, ContextMenuStrip cmsInitiationNotes, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treMetamagic == null || notifyCollectionChangedEventArgs == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                switch (notifyCollectionChangedEventArgs.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                    {
                        foreach (Art objArt in notifyCollectionChangedEventArgs.NewItems)
                        {
                            await AddToTree(objArt);
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Remove:
                    {
                        await treMetamagic.DoThreadSafeAsync(x =>
                        {
                            foreach (Art objArt in notifyCollectionChangedEventArgs.OldItems)
                            {
                                x.FindNodeByTag(objArt)?.Remove();
                            }
                        }, token);
                    }
                        break;

                    case NotifyCollectionChangedAction.Replace:
                    {
                        await treMetamagic.DoThreadSafeAsync(x =>
                        {
                            foreach (Art objArt in notifyCollectionChangedEventArgs.OldItems)
                            {
                                x.FindNodeByTag(objArt)?.Remove();
                            }
                        }, token);

                        foreach (Art objArt in notifyCollectionChangedEventArgs.NewItems)
                        {
                            await AddToTree(objArt);
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Reset:
                    {
                        await RefreshInitiationGrades(treMetamagic, cmsMetamagic, cmsInitiationNotes, token: token);
                    }
                        break;
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }

            async ValueTask AddToTree(Art objArt, bool blnSingleAdd = true)
            {
                InitiationGrade objGrade = await CharacterObject.InitiationGrades.FirstOrDefaultAsync(x => x.Grade == objArt.Grade, token);

                if (objGrade != null)
                {
                    await treMetamagic.DoThreadSafeAsync(x =>
                    {
                        TreeNode nodMetamagicParent = x.FindNodeByTag(objGrade);
                        if (nodMetamagicParent != null)
                        {
                            TreeNodeCollection nodMetamagicParentChildren = nodMetamagicParent.Nodes;
                            TreeNode objNode = objArt.CreateTreeNode(cmsInitiationNotes, true);
                            if (objNode == null)
                                return;
                            int intNodesCount = nodMetamagicParentChildren.Count;
                            int intTargetIndex = 0;
                            for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                            {
                                if (CompareTreeNodes.CompareText(nodMetamagicParentChildren[intTargetIndex], objNode)
                                    >= 0)
                                {
                                    break;
                                }
                            }

                            nodMetamagicParentChildren.Insert(intTargetIndex, objNode);
                            nodMetamagicParent.Expand();
                            if (blnSingleAdd)
                                x.SelectedNode = objNode;
                        }
                    }, token);
                }
            }
        }

        protected async ValueTask RefreshEnhancementCollection(TreeView treMetamagic, ContextMenuStrip cmsMetamagic, ContextMenuStrip cmsInitiationNotes, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treMetamagic == null || notifyCollectionChangedEventArgs == null)
                return;

            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                switch (notifyCollectionChangedEventArgs.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                    {
                        foreach (Enhancement objEnhancement in notifyCollectionChangedEventArgs.NewItems)
                        {
                            await AddToTree(objEnhancement);
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Remove:
                    {
                        await treMetamagic.DoThreadSafeAsync(x =>
                        {
                            foreach (Enhancement objEnhancement in notifyCollectionChangedEventArgs.OldItems)
                            {
                                x.FindNodeByTag(objEnhancement)?.Remove();
                            }
                        }, token);
                    }
                        break;

                    case NotifyCollectionChangedAction.Replace:
                    {
                        await treMetamagic.DoThreadSafeAsync(x =>
                        {
                            foreach (Enhancement objEnhancement in notifyCollectionChangedEventArgs.OldItems)
                            {
                                x.FindNodeByTag(objEnhancement)?.Remove();
                            }
                        }, token);

                        foreach (Enhancement objEnhancement in notifyCollectionChangedEventArgs.NewItems)
                        {
                            await AddToTree(objEnhancement);
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Reset:
                    {
                        await RefreshInitiationGrades(treMetamagic, cmsMetamagic, cmsInitiationNotes, token: token);
                    }
                        break;
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }

            async ValueTask AddToTree(Enhancement objEnhancement, bool blnSingleAdd = true)
            {
                InitiationGrade objGrade = await CharacterObject.InitiationGrades.FirstOrDefaultAsync(x => x.Grade == objEnhancement.Grade, token);

                if (objGrade != null)
                {
                    await treMetamagic.DoThreadSafeAsync(x =>
                    {
                        TreeNode nodMetamagicParent = x.FindNodeByTag(objGrade);
                        if (nodMetamagicParent != null)
                        {
                            TreeNodeCollection nodMetamagicParentChildren = nodMetamagicParent.Nodes;
                            TreeNode objNode = objEnhancement.CreateTreeNode(cmsInitiationNotes, true);
                            if (objNode == null)
                                return;
                            int intNodesCount = nodMetamagicParentChildren.Count;
                            int intTargetIndex = 0;
                            for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                            {
                                if (CompareTreeNodes.CompareText(nodMetamagicParentChildren[intTargetIndex], objNode)
                                    >= 0)
                                {
                                    break;
                                }
                            }

                            nodMetamagicParentChildren.Insert(intTargetIndex, objNode);
                            nodMetamagicParent.Expand();
                            if (blnSingleAdd)
                                x.SelectedNode = objNode;
                        }
                    }, token);
                }
            }
        }

        protected async ValueTask RefreshPowerCollectionListChanged(TreeView treMetamagic, ContextMenuStrip cmsMetamagic, ContextMenuStrip cmsInitiationNotes, ListChangedEventArgs e = null, CancellationToken token = default)
        {
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                switch (e?.ListChangedType)
                {
                    case ListChangedType.ItemAdded:
                    {
                        await CharacterObject.Powers[e.NewIndex].Enhancements
                                             .AddTaggedCollectionChangedAsync(
                                                 treMetamagic, MakeDirtyWithCharacterUpdate, token);
                        await CharacterObject.Powers[e.NewIndex].Enhancements
                                             .AddTaggedCollectionChangedAsync(treMetamagic, FuncDelegateToAdd, token);

                        async void FuncDelegateToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                            await RefreshEnhancementCollection(treMetamagic, cmsMetamagic, cmsInitiationNotes,
                                                               y, token);
                    }
                        break;

                    case ListChangedType.Reset:
                    {
                        await RefreshInitiationGrades(treMetamagic, cmsMetamagic, cmsInitiationNotes, token: token);
                    }
                        break;

                    case ListChangedType.ItemDeleted:
                    case ListChangedType.ItemChanged:
                        break;
                    case ListChangedType.ItemMoved:
                    case ListChangedType.PropertyDescriptorAdded:
                    case ListChangedType.PropertyDescriptorDeleted:
                    case ListChangedType.PropertyDescriptorChanged:
                        return;
                    case null:
                    {
                        foreach (Power objPower in CharacterObject.Powers)
                        {
                            await objPower.Enhancements.AddTaggedCollectionChangedAsync(treMetamagic,
                                MakeDirtyWithCharacterUpdate, token);
                            await objPower.Enhancements.AddTaggedCollectionChangedAsync(
                                treMetamagic, FuncDelegateToAdd, token);

                            async void FuncDelegateToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await RefreshEnhancementCollection(treMetamagic, cmsMetamagic, cmsInitiationNotes,
                                                                   y, token);
                        }
                    }
                        break;
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }

            await RequestCharacterUpdate(token);

            await SetDirty(true, token);
        }

        protected async ValueTask RefreshPowerCollectionBeforeRemove(TreeView treMetamagic, RemovingOldEventArgs removingOldEventArgs, CancellationToken token = default)
        {
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (removingOldEventArgs?.OldObject is Power objPower)
                {
                    await objPower.Enhancements.RemoveTaggedCollectionChangedAsync(treMetamagic, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshMetamagicCollection(TreeView treMetamagic, ContextMenuStrip cmsMetamagic, ContextMenuStrip cmsInitiationNotes, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treMetamagic == null || notifyCollectionChangedEventArgs == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                switch (notifyCollectionChangedEventArgs.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                    {
                        foreach (Metamagic objMetamagic in notifyCollectionChangedEventArgs.NewItems)
                        {
                            await AddToTree(objMetamagic);
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Remove:
                    {
                        await treMetamagic.DoThreadSafeAsync(x =>
                        {
                            foreach (Metamagic objMetamagic in notifyCollectionChangedEventArgs.OldItems)
                            {
                                x.FindNodeByTag(objMetamagic)?.Remove();
                            }
                        }, token);
                    }
                        break;

                    case NotifyCollectionChangedAction.Replace:
                    {
                        await treMetamagic.DoThreadSafeAsync(x =>
                        {
                            foreach (Metamagic objMetamagic in notifyCollectionChangedEventArgs.OldItems)
                            {
                                x.FindNodeByTag(objMetamagic)?.Remove();
                            }
                        }, token);

                        foreach (Metamagic objMetamagic in notifyCollectionChangedEventArgs.NewItems)
                        {
                            await AddToTree(objMetamagic);
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Reset:
                    {
                        await RefreshInitiationGrades(treMetamagic, cmsMetamagic, cmsInitiationNotes, token: token);
                    }
                        break;
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }

            async ValueTask AddToTree(Metamagic objMetamagic, bool blnSingleAdd = true)
            {
                if (objMetamagic.Grade < 0)
                {
                    await treMetamagic.DoThreadSafeAsync(x =>
                    {
                        TreeNodeCollection nodMetamagicParentChildren = x.Nodes;
                        TreeNode objNode = objMetamagic.CreateTreeNode(cmsInitiationNotes, true);
                        if (objNode == null)
                            return;
                        int intNodesCount = nodMetamagicParentChildren.Count;
                        int intTargetIndex = CharacterObject.InitiationGrades.Count;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(nodMetamagicParentChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }

                        nodMetamagicParentChildren.Insert(intTargetIndex, objNode);
                        objNode.Expand();
                        if (blnSingleAdd)
                            x.SelectedNode = objNode;
                    }, token);
                }
                else
                {
                    InitiationGrade objGrade = await CharacterObject.InitiationGrades.FirstOrDefaultAsync(x => x.Grade == objMetamagic.Grade, token);

                    if (objGrade != null)
                    {
                        await treMetamagic.DoThreadSafeAsync(x =>
                        {
                            TreeNode nodMetamagicParent = x.FindNodeByTag(objGrade);
                            if (nodMetamagicParent != null)
                            {
                                TreeNodeCollection nodMetamagicParentChildren = nodMetamagicParent.Nodes;
                                TreeNode objNode = objMetamagic.CreateTreeNode(cmsInitiationNotes, true);
                                if (objNode == null)
                                    return;
                                int intNodesCount = nodMetamagicParentChildren.Count;
                                int intTargetIndex = 0;
                                for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                                {
                                    if (CompareTreeNodes.CompareText(nodMetamagicParentChildren[intTargetIndex],
                                                                     objNode) >= 0)
                                    {
                                        break;
                                    }
                                }

                                nodMetamagicParentChildren.Insert(intTargetIndex, objNode);
                                objNode.Expand();
                                if (blnSingleAdd)
                                    x.SelectedNode = objNode;
                            }
                        }, token);
                    }
                }
            }
        }

        /// <summary>
        /// Clears and updates the TreeView for Critter Powers. Typically called as part of AddQuality or UpdateCharacterInfo.
        /// </summary>
        /// <param name="treCritterPowers">TreeNode that will be cleared and populated.</param>
        /// <param name="cmsCritterPowers">ContextMenuStrip that will be added to each power.</param>
        /// <param name="notifyCollectionChangedEventArgs">Arguments for the change to the underlying ObservableCollection.</param>
        /// <param name="token">Cancellation token to listen to.</param>
        protected async ValueTask RefreshCritterPowers(TreeView treCritterPowers, ContextMenuStrip cmsCritterPowers, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treCritterPowers == null)
                return;
            TreeNode objPowersNode = null;
            TreeNode objWeaknessesNode = null;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (notifyCollectionChangedEventArgs == null
                    || notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    string strSelectedId
                        = (await treCritterPowers.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                              IHasInternalId)
                          ?.InternalId ??
                          string.Empty;
                    await treCritterPowers.DoThreadSafeAsync(x => x.Nodes.Clear(), token);
                    // Add the Critter Powers that exist.
                    foreach (CritterPower objPower in CharacterObject.CritterPowers)
                    {
                        await AddToTree(objPower, false);
                    }

                    await treCritterPowers.DoThreadSafeAsync(x => x.SortCustomAlphabetically(strSelectedId),
                                                             token);
                }
                else
                {
                    await treCritterPowers.DoThreadSafeAsync(x =>
                    {
                        objPowersNode = x.FindNode("Node_CritterPowers", false);
                        objWeaknessesNode = x.FindNode("Node_CritterWeaknesses", false);
                    }, token);
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (CritterPower objPower in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objPower);
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Remove:
                        {
                            await treCritterPowers.DoThreadSafeAsync(x =>
                            {
                                foreach (CritterPower objPower in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    TreeNode objNode = x.FindNodeByTag(objPower);
                                    if (objNode != null)
                                    {
                                        TreeNode objParent = objNode.Parent;
                                        objNode.Remove();
                                        if (objParent.Level == 0 && objParent.Nodes.Count == 0)
                                            objParent.Remove();
                                    }
                                }
                            }, token);

                            break;
                        }
                        case NotifyCollectionChangedAction.Replace:
                        {
                            List<TreeNode> lstOldParents =
                                new List<TreeNode>(notifyCollectionChangedEventArgs.OldItems.Count);
                            await treCritterPowers.DoThreadSafeAsync(x =>
                            {
                                foreach (CritterPower objPower in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    TreeNode objNode = x.FindNode(objPower.InternalId);
                                    if (objNode != null)
                                    {
                                        lstOldParents.Add(objNode.Parent);
                                        objNode.Remove();
                                    }
                                }
                            }, token);

                            foreach (CritterPower objPower in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objPower);
                            }

                            await treCritterPowers.DoThreadSafeAsync(() =>
                            {
                                foreach (TreeNode objOldParent in lstOldParents)
                                {
                                    if (objOldParent.Level == 0 && objOldParent.Nodes.Count == 0)
                                        objOldParent.Remove();
                                }
                            }, token);

                            break;
                        }
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }

            async ValueTask AddToTree(CritterPower objPower, bool blnSingleAdd = true)
            {
                TreeNode objNode = objPower.CreateTreeNode(cmsCritterPowers);
                if (objNode == null)
                    return;
                TreeNode objParentNode;
                switch (objPower.Category)
                {
                    case "Weakness":
                        if (objWeaknessesNode == null)
                        {
                            objWeaknessesNode = new TreeNode
                            {
                                Tag = "Node_CritterWeaknesses",
                                Text = await LanguageManager.GetStringAsync("Node_CritterWeaknesses", token: token)
                            };
                            await treCritterPowers.DoThreadSafeAsync(x =>
                            {
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Add(objWeaknessesNode);
                                objWeaknessesNode.Expand();
                            }, token);
                        }
                        objParentNode = objWeaknessesNode;
                        break;

                    default:
                        if (objPowersNode == null)
                        {
                            objPowersNode = new TreeNode
                            {
                                Tag = "Node_CritterPowers",
                                Text = await LanguageManager.GetStringAsync("Node_CritterPowers", token: token)
                            };
                            await treCritterPowers.DoThreadSafeAsync(x =>
                            {
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Insert(0, objPowersNode);
                                objPowersNode.Expand();
                            }, token);
                        }
                        objParentNode = objPowersNode;
                        break;
                }

                await treCritterPowers.DoThreadSafeAsync(x =>
                {
                    if (objParentNode == null)
                        return;
                    if (blnSingleAdd)
                    {
                        TreeNodeCollection lstParentNodeChildren = objParentNode.Nodes;
                        int intNodesCount = lstParentNodeChildren.Count;
                        int intTargetIndex = 0;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }

                        lstParentNodeChildren.Insert(intTargetIndex, objNode);
                        x.SelectedNode = objNode;
                    }
                    else
                        objParentNode.Nodes.Add(objNode);
                }, token);
            }
        }

        /// <summary>
        /// Refreshes the list of qualities into the selected TreeNode. If the same number of
        /// </summary>
        /// <param name="treQualities">TreeView to insert the qualities into.</param>
        /// <param name="cmsQuality">ContextMenuStrip to add to each Quality node.</param>
        /// <param name="notifyCollectionChangedEventArgs">Arguments for the change to the underlying ObservableCollection.</param>
        /// <param name="token">Cancellation token to listen to.</param>
        protected async ValueTask RefreshQualities(TreeView treQualities, ContextMenuStrip cmsQuality, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treQualities == null)
                return;
            TreeNode objPositiveQualityRoot = null;
            TreeNode objNegativeQualityRoot = null;
            TreeNode objLifeModuleRoot = null;

            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    string strSelectedNode =
                        (await treQualities.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                            IHasInternalId)?.InternalId ?? string.Empty;

                    // Create the root nodes.
                    foreach (Quality objQuality in CharacterObject.Qualities)
                        objQuality.PropertyChanged -= AddedQualityOnPropertyChanged;
                    await treQualities.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                    // Multiple instances of the same quality are combined into just one entry with a number next to it (e.g. 6 discrete entries of "Focused Concentration" become "Focused Concentration 6")
                    using (new FetchSafelyFromPool<HashSet<string>>(Utils.StringHashSetPool,
                                                                    out HashSet<string> setQualitiesToPrint))
                    {
                        foreach (Quality objQuality in CharacterObject.Qualities)
                        {
                            setQualitiesToPrint.Add(objQuality.SourceIDString + '|' +
                                                    await objQuality.GetSourceNameAsync(GlobalSettings.Language, token) + '|' +
                                                    objQuality.Extra);
                        }

                        // Add Qualities
                        foreach (Quality objQuality in CharacterObject.Qualities)
                        {
                            if (!setQualitiesToPrint.Remove(objQuality.SourceIDString + '|' +
                                                            await objQuality.GetSourceNameAsync(GlobalSettings.Language, token)
                                                            + '|' +
                                                            objQuality.Extra))
                                continue;

                            await AddToTree(objQuality, false);
                        }
                    }

                    await treQualities.DoThreadSafeAsync(x => x.SortCustomAlphabetically(strSelectedNode),
                                                         token);
                }
                else
                {
                    await treQualities.DoThreadSafeAsync(x =>
                    {
                        objPositiveQualityRoot = x.FindNodeByTag("Node_SelectedPositiveQualities", false);
                        objNegativeQualityRoot = x.FindNodeByTag("Node_SelectedNegativeQualities", false);
                        objLifeModuleRoot = x.FindNodeByTag("String_LifeModules", false);
                    }, token);
                    bool blnDoNameRefresh = false;
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (Quality objQuality in notifyCollectionChangedEventArgs.NewItems)
                            {
                                if (objQuality.Levels > 1)
                                    blnDoNameRefresh = true;
                                else
                                    await AddToTree(objQuality);
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (Quality objQuality in notifyCollectionChangedEventArgs.OldItems)
                            {
                                if (objQuality.Levels > 0)
                                    blnDoNameRefresh = true;
                                else
                                {
                                    await treQualities.DoThreadSafeAsync(x =>
                                    {
                                        TreeNode objNode = x.FindNodeByTag(objQuality);
                                        if (objNode != null)
                                        {
                                            TreeNode objParent = objNode.Parent;
                                            objNode.Remove();
                                            objQuality.PropertyChanged -= AddedQualityOnPropertyChanged;
                                            if (objParent.Level == 0 && objParent.Nodes.Count == 0)
                                                objParent.Remove();
                                        }
                                    }, token);
                                }
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Replace:
                        {
                            List<TreeNode> lstOldParents =
                                new List<TreeNode>(notifyCollectionChangedEventArgs.OldItems.Count);
                            foreach (Quality objQuality in notifyCollectionChangedEventArgs.OldItems)
                            {
                                if (objQuality.Levels > 0)
                                    blnDoNameRefresh = true;
                                else
                                {
                                    TreeNode objNode
                                        = await treQualities.DoThreadSafeFuncAsync(
                                            x => x.FindNodeByTag(objQuality), token);
                                    if (objNode != null)
                                    {
                                        await treQualities.DoThreadSafeAsync(() =>
                                        {
                                            if (objNode.Parent != null)
                                                lstOldParents.Add(objNode.Parent);
                                            objNode.Remove();
                                            objQuality.PropertyChanged -= AddedQualityOnPropertyChanged;
                                        }, token);
                                    }
                                    else
                                    {
                                        await RefreshQualityNames(treQualities, token);
                                    }
                                }
                            }

                            foreach (Quality objQuality in notifyCollectionChangedEventArgs.NewItems)
                            {
                                if (objQuality.Levels > 1)
                                    blnDoNameRefresh = true;
                                else
                                    await AddToTree(objQuality);
                            }

                            await treQualities.DoThreadSafeAsync(() =>
                            {
                                foreach (TreeNode objOldParent in lstOldParents)
                                {
                                    if (objOldParent.Level == 0 && objOldParent.Nodes.Count == 0)
                                        objOldParent.Remove();
                                }
                            }, token);

                            break;
                        }
                    }

                    if (blnDoNameRefresh)
                        await RefreshQualityNames(treQualities, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }

            async ValueTask AddToTree(Quality objQuality, bool blnSingleAdd = true)
            {
                TreeNode objNode = objQuality.CreateTreeNode(cmsQuality, treQualities);
                if (objNode == null)
                    return;
                TreeNode objParentNode = null;
                switch (objQuality.Type)
                {
                    case QualityType.Positive:
                        if (objPositiveQualityRoot == null)
                        {
                            objPositiveQualityRoot = new TreeNode
                            {
                                Tag = "Node_SelectedPositiveQualities",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedPositiveQualities", token: token)
                            };
                            await treQualities.DoThreadSafeAsync(x =>
                            {
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Insert(0, objPositiveQualityRoot);
                                objPositiveQualityRoot.Expand();
                            }, token);
                        }
                        objParentNode = objPositiveQualityRoot;
                        break;

                    case QualityType.Negative:
                        if (objNegativeQualityRoot == null)
                        {
                            objNegativeQualityRoot = new TreeNode
                            {
                                Tag = "Node_SelectedNegativeQualities",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedNegativeQualities", token: token)
                            };
                            await treQualities.DoThreadSafeAsync(x =>
                            {
                                x.Nodes.Insert(
                                    objLifeModuleRoot != null
                                    && objPositiveQualityRoot == null
                                        ? 0
                                        // ReSharper disable once AssignNullToNotNullAttribute
                                        : 1, objNegativeQualityRoot);
                                objNegativeQualityRoot.Expand();
                            }, token);
                        }
                        objParentNode = objNegativeQualityRoot;
                        break;

                    case QualityType.LifeModule:
                        if (objLifeModuleRoot == null)
                        {
                            objLifeModuleRoot = new TreeNode
                            {
                                Tag = "String_LifeModules",
                                Text = await LanguageManager.GetStringAsync("String_LifeModules", token: token)
                            };
                            await treQualities.DoThreadSafeAsync(x =>
                            {
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Add(objLifeModuleRoot);
                                objLifeModuleRoot.Expand();
                            }, token);
                        }
                        objParentNode = objLifeModuleRoot;
                        break;
                }

                if (objParentNode != null)
                {
                    await treQualities.DoThreadSafeAsync(x =>
                    {
                        if (blnSingleAdd)
                        {
                            TreeNodeCollection lstParentNodeChildren = objParentNode.Nodes;
                            int intNodesCount = lstParentNodeChildren.Count;
                            int intTargetIndex = 0;
                            for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                            {
                                if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                                {
                                    break;
                                }
                            }

                            lstParentNodeChildren.Insert(intTargetIndex, objNode);
                            x.SelectedNode = objNode;
                        }
                        else
                            objParentNode.Nodes.Add(objNode);
                    }, token);
                    objQuality.PropertyChanged += AddedQualityOnPropertyChanged;
                }
            }

            void AddedQualityOnPropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                switch (e.PropertyName)
                {
                    case nameof(Quality.Suppressed):
                        {
                            if (!(sender is Quality objQuality))
                                return;
                            TreeNode objNode = treQualities.FindNodeByTag(objQuality);
                            if (objNode == null)
                                return;
                            Font objOldFont = objNode.NodeFont;
                            //Treenodes store their font as null when inheriting from the treeview; have to pull it from the treeview directly to set the fontstyle.
                            objNode.NodeFont = new Font(treQualities.Font,
                                objQuality.Suppressed ? FontStyle.Strikeout : FontStyle.Regular);
                            // Dispose the old font if it's not null so that we don't leak memory
                            objOldFont?.Dispose();
                            break;
                        }
                    case nameof(Quality.Notes):
                        {
                            if (!(sender is Quality objQuality))
                                return;
                            TreeNode objNode = treQualities.FindNodeByTag(objQuality);
                            if (objNode == null)
                                return;
                            objNode.ToolTipText = objQuality.Notes.WordWrap();
                            break;
                        }
                }
            }
        }

        /// <summary>
        /// Refreshes all the names of qualities in the nodes
        /// </summary>
        /// <param name="treQualities">TreeView to insert the qualities into.</param>
        /// <param name="token">Cancellation token to use.</param>
        protected async ValueTask RefreshQualityNames(TreeView treQualities, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (treQualities == null || await treQualities.DoThreadSafeFuncAsync(x => x.GetNodeCount(false), token) <= 0)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                await treQualities.DoThreadSafeAsync(x =>
                {
                    TreeNode objSelectedNode = x.SelectedNode;
                    foreach (TreeNode objQualityTypeNode in x.Nodes)
                    {
                        foreach (TreeNode objQualityNode in objQualityTypeNode.Nodes)
                        {
                            objQualityNode.Text = ((Quality) objQualityNode.Tag).CurrentDisplayName;
                        }
                    }

                    x.SortCustomAlphabetically(objSelectedNode?.Tag);
                }, token);
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        #endregion Refresh Treeviews and Panels

        /// <summary>
        /// Method for removing old <addqualities /> nodes from existing characters.
        /// </summary>
        /// <param name="objNodeList">XmlNode to load. Expected to be addqualities/addquality</param>
        /// <param name="token">CancellationToken to listen to.</param>
        protected async ValueTask RemoveAddedQualities(XPathNodeIterator objNodeList, CancellationToken token = default)
        {
            if (objNodeList == null || objNodeList.Count <= 0)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                foreach (XPathNavigator objNode in objNodeList)
                {
                    Quality objQuality = await CharacterObject.Qualities.FirstOrDefaultAsync(x => x.Name == objNode.Value, token);
                    if (objQuality != null)
                    {
                        objQuality.DeleteQuality();
                        await ImprovementManager.RemoveImprovementsAsync(
                            CharacterObject, Improvement.ImprovementSource.CritterPower,
                            objQuality.InternalId, token);
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        #region Locations

        protected async ValueTask RefreshArmorLocations(TreeView treArmor, ContextMenuStrip cmsArmorLocation, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treArmor == null || notifyCollectionChangedEventArgs == null)
                return;

            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treArmor.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as IHasInternalId)
                    ?.InternalId ?? string.Empty;

                TreeNode nodRoot
                    = await treArmor.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedArmor", false), token);
                await RefreshLocation(treArmor, nodRoot, cmsArmorLocation, notifyCollectionChangedEventArgs,
                                      strSelectedId,
                                      "Node_SelectedArmor", token);
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshGearLocations(TreeView treGear, ContextMenuStrip cmsGearLocation, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treGear == null || notifyCollectionChangedEventArgs == null)
                return;

            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treGear.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as IHasInternalId)
                    ?.InternalId ?? string.Empty;

                TreeNode nodRoot
                    = await treGear.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedGear", false), token);
                await RefreshLocation(treGear, nodRoot, cmsGearLocation, notifyCollectionChangedEventArgs,
                                      strSelectedId,
                                      "Node_SelectedGear", token);
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshVehicleLocations(TreeView treVehicles, ContextMenuStrip cmsVehicleLocation, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treVehicles == null || notifyCollectionChangedEventArgs == null)
                return;

            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treVehicles.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag,
                                                               token) as IHasInternalId)?.InternalId
                      ?? string.Empty;

                TreeNode nodRoot
                    = await treVehicles.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedVehicles", false),
                                                              token);
                await RefreshLocation(treVehicles, nodRoot, cmsVehicleLocation, notifyCollectionChangedEventArgs,
                                      strSelectedId,
                                      "Node_SelectedVehicles", token);
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshLocationsInVehicle(TreeView treVehicles, Vehicle objVehicle, ContextMenuStrip cmsVehicleLocation, Func<int> funcOffset, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treVehicles == null || objVehicle == null || notifyCollectionChangedEventArgs == null)
                return;

            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treVehicles.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag,
                                                               token) as IHasInternalId)?.InternalId
                      ?? string.Empty;

                TreeNode nodRoot
                    = await treVehicles.DoThreadSafeFuncAsync(x => x.FindNodeByTag(objVehicle), token);
                await RefreshLocation(treVehicles, nodRoot, cmsVehicleLocation, funcOffset,
                                      notifyCollectionChangedEventArgs,
                                      strSelectedId, "Node_SelectedVehicles", false, token);
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshWeaponLocations(TreeView treWeapons, ContextMenuStrip cmsWeaponLocation, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treWeapons == null || notifyCollectionChangedEventArgs == null)
                return;

            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treWeapons.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as IHasInternalId)
                    ?.InternalId ?? string.Empty;

                TreeNode nodRoot
                    = await treWeapons.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedWeapons", false),
                                                             token);
                await RefreshLocation(treWeapons, nodRoot, cmsWeaponLocation, notifyCollectionChangedEventArgs,
                                      strSelectedId,
                                      "Node_SelectedWeapons", token);
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshCustomImprovementLocations(TreeView treImprovements, ContextMenuStrip cmsImprovementLocation, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treImprovements == null || notifyCollectionChangedEventArgs == null)
                return;

            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId =
                    (await treImprovements.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                        IHasInternalId)?.InternalId ?? string.Empty;

                TreeNode nodRoot
                    = await treImprovements.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedImprovements", false),
                                                                  token);

                switch (notifyCollectionChangedEventArgs.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                    {
                        int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                        foreach (string strLocation in notifyCollectionChangedEventArgs.NewItems)
                        {
                            TreeNode objLocation = new TreeNode
                            {
                                Tag = strLocation,
                                Text = strLocation,
                                ContextMenuStrip = cmsImprovementLocation
                            };
                            await treImprovements.DoThreadSafeAsync(x => x.Nodes.Insert(intNewIndex, objLocation),
                                                                    token);
                            ++intNewIndex;
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Remove:
                    {
                        foreach (string strLocation in notifyCollectionChangedEventArgs.OldItems)
                        {
                            TreeNode objNode
                                = await treImprovements.DoThreadSafeFuncAsync(
                                    x => x.FindNodeByTag(strLocation, false), token);
                            if (objNode != null)
                            {
                                await treImprovements.DoThreadSafeAsync(() => objNode.Remove(), token);
                                if (objNode.Nodes.Count > 0)
                                {
                                    if (nodRoot == null)
                                    {
                                        nodRoot = new TreeNode
                                        {
                                            Tag = "Node_SelectedImprovements",
                                            Text = await LanguageManager.GetStringAsync("Node_SelectedImprovements", token: token)
                                        };
                                        await treImprovements.DoThreadSafeAsync(
                                            x => x.Nodes.Insert(0, nodRoot), token);
                                    }

                                    await treImprovements.DoThreadSafeAsync(() =>
                                    {
                                        for (int i = objNode.Nodes.Count - 1; i >= 0; --i)
                                        {
                                            TreeNode nodImprovement = objNode.Nodes[i];
                                            nodImprovement.Remove();
                                            nodRoot.Nodes.Add(nodImprovement);
                                        }
                                    }, token);
                                }
                            }
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Replace:
                    {
                        int intNewItemsIndex = 0;
                        foreach (string strLocation in notifyCollectionChangedEventArgs.OldItems)
                        {
                            TreeNode objNode
                                = await treImprovements.DoThreadSafeFuncAsync(
                                    x => x.FindNodeByTag(strLocation, false), token);
                            if (objNode != null)
                            {
                                if (notifyCollectionChangedEventArgs
                                        .NewItems[intNewItemsIndex] is string objNewLocation)
                                {
                                    await treImprovements.DoThreadSafeAsync(() =>
                                    {
                                        objNode.Tag = objNewLocation;
                                        objNode.Text = objNewLocation;
                                    }, token);
                                }

                                ++intNewItemsIndex;
                            }
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Move:
                    {
                        List<Tuple<string, TreeNode>> lstMoveNodes =
                            new List<Tuple<string, TreeNode>>(notifyCollectionChangedEventArgs.OldItems.Count);
                        foreach (string strLocation in notifyCollectionChangedEventArgs.OldItems)
                        {
                            TreeNode objLocation
                                = await treImprovements.DoThreadSafeFuncAsync(
                                    x => x.FindNode(strLocation, false), token);
                            if (objLocation != null)
                            {
                                lstMoveNodes.Add(new Tuple<string, TreeNode>(strLocation, objLocation));
                                objLocation.Remove();
                            }
                        }

                        int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                        foreach (string strLocation in notifyCollectionChangedEventArgs.NewItems)
                        {
                            Tuple<string, TreeNode> objLocationTuple =
                                lstMoveNodes.Find(x => x.Item1 == strLocation);
                            if (objLocationTuple != null)
                            {
                                await treImprovements.DoThreadSafeAsync(
                                    x => x.Nodes.Insert(intNewIndex, objLocationTuple.Item2), token);
                                ++intNewIndex;
                                lstMoveNodes.Remove(objLocationTuple);
                            }
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Reset:
                    {
                        foreach (string strLocation in CharacterObject.ImprovementGroups)
                        {
                            TreeNode objLocation
                                = await treImprovements.DoThreadSafeFuncAsync(
                                    x => x.FindNode(strLocation, false), token);
                            if (objLocation != null)
                            {
                                await treImprovements.DoThreadSafeAsync(() => objLocation.Remove(), token);
                                if (objLocation.Nodes.Count > 0)
                                {
                                    if (nodRoot == null)
                                    {
                                        nodRoot = new TreeNode
                                        {
                                            Tag = "Node_SelectedImprovements",
                                            Text = await LanguageManager.GetStringAsync("Node_SelectedImprovements", token: token)
                                        };
                                        await treImprovements.DoThreadSafeAsync(
                                            x => x.Nodes.Insert(0, nodRoot), token);
                                    }

                                    await treImprovements.DoThreadSafeAsync(() =>
                                    {
                                        for (int i = objLocation.Nodes.Count - 1; i >= 0; --i)
                                        {
                                            TreeNode nodImprovement = objLocation.Nodes[i];
                                            nodImprovement.Remove();
                                            nodRoot.Nodes.Add(nodImprovement);
                                        }
                                    }, token);
                                }
                            }
                        }
                    }
                        break;
                }

                await treImprovements.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId), token);
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        private async ValueTask RefreshLocation(TreeView treSelected, TreeNode nodRoot, ContextMenuStrip cmsLocation,
            NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, string strSelectedId, string strNodeName, CancellationToken token = default)
        {
            await RefreshLocation(treSelected, nodRoot, cmsLocation, null, notifyCollectionChangedEventArgs, strSelectedId, strNodeName, token: token);
        }

        private async ValueTask RefreshLocation(TreeView treSelected, TreeNode nodRoot, ContextMenuStrip cmsLocation,
            Func<int> funcOffset,
            NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, string strSelectedId, string strNodeName,
            bool rootSibling = true, CancellationToken token = default)
        {
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                switch (notifyCollectionChangedEventArgs.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                    {
                        int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                        if (funcOffset != null)
                            intNewIndex += funcOffset.Invoke();
                        await treSelected.DoThreadSafeAsync(x =>
                        {
                            foreach (Location objLocation in notifyCollectionChangedEventArgs.NewItems)
                            {
                                if (rootSibling)
                                {
                                    x.Nodes.Insert(intNewIndex, objLocation.CreateTreeNode(cmsLocation));
                                }
                                else
                                {
                                    nodRoot.Nodes.Insert(intNewIndex, objLocation.CreateTreeNode(cmsLocation));
                                }

                                ++intNewIndex;
                            }
                        }, token);
                    }
                        break;

                    case NotifyCollectionChangedAction.Remove:
                    {
                        foreach (Location objLocation in notifyCollectionChangedEventArgs.OldItems)
                        {
                            TreeNode nodLocation
                                = await treSelected.DoThreadSafeFuncAsync(
                                    x => x.FindNodeByTag(objLocation, false), token);
                            if (nodLocation == null)
                                continue;
                            if (nodLocation.Nodes.Count > 0)
                            {
                                if (nodRoot == null)
                                {
                                    nodRoot = new TreeNode
                                    {
                                        Tag = strNodeName,
                                        Text = await LanguageManager.GetStringAsync(strNodeName, token: token)
                                    };
                                    await treSelected.DoThreadSafeAsync(x => x.Nodes.Insert(0, nodRoot), token);
                                }

                                await treSelected.DoThreadSafeAsync(() =>
                                {
                                    for (int i = nodLocation.Nodes.Count - 1; i >= 0; --i)
                                    {
                                        TreeNode nodWeapon = nodLocation.Nodes[i];
                                        nodWeapon.Remove();
                                        nodRoot.Nodes.Add(nodWeapon);
                                    }
                                }, token);
                            }

                            await treSelected.DoThreadSafeAsync(() => nodLocation.Remove(), token);
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Replace:
                    {
                        int intNewItemsIndex = 0;
                        foreach (Location objLocation in notifyCollectionChangedEventArgs.OldItems)
                        {
                            TreeNode objNode
                                = await treSelected.DoThreadSafeFuncAsync(
                                    x => x.FindNodeByTag(objLocation, false), token);
                            if (objNode != null)
                            {
                                if (notifyCollectionChangedEventArgs.NewItems[intNewItemsIndex] is Location
                                    objNewLocation)
                                {
                                    await treSelected.DoThreadSafeAsync(() =>
                                    {
                                        objNode.Tag = objNewLocation;
                                        objNode.Text = objNewLocation.DisplayName();
                                    }, token);
                                }

                                ++intNewItemsIndex;
                            }
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Move:
                    {
                        List<Tuple<Location, TreeNode>> lstMoveNodes =
                            new List<Tuple<Location, TreeNode>>(notifyCollectionChangedEventArgs.OldItems.Count);
                        foreach (Location objLocation in notifyCollectionChangedEventArgs.OldItems)
                        {
                            TreeNode objNode
                                = await treSelected.DoThreadSafeFuncAsync(
                                    x => x.FindNodeByTag(objLocation, false), token);
                            if (objNode != null)
                            {
                                lstMoveNodes.Add(new Tuple<Location, TreeNode>(objLocation, objNode));
                                objLocation.Remove(false);
                            }
                        }

                        int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                        foreach (Location objLocation in notifyCollectionChangedEventArgs.NewItems)
                        {
                            Tuple<Location, TreeNode> objLocationTuple =
                                lstMoveNodes.Find(x => x.Item1 == objLocation);
                            if (objLocationTuple != null)
                            {
                                await treSelected.DoThreadSafeAsync(
                                    x => x.Nodes.Insert(intNewIndex, objLocationTuple.Item2), token);
                                ++intNewIndex;
                                lstMoveNodes.Remove(objLocationTuple);
                            }
                        }
                    }
                        break;

                    case NotifyCollectionChangedAction.Reset:
                    {
                        List<Location> lstLocations = new List<Location>(
                            CharacterObject.ArmorLocations.Count + CharacterObject.WeaponLocations.Count
                                                                 + CharacterObject.GearLocations.Count
                                                                 + CharacterObject.VehicleLocations.Count
                                                                 + CharacterObject.Vehicles.Count);
                        lstLocations.AddRange(CharacterObject.ArmorLocations);
                        lstLocations.AddRange(CharacterObject.WeaponLocations);
                        lstLocations.AddRange(CharacterObject.GearLocations);
                        lstLocations.AddRange(CharacterObject.VehicleLocations);
                        lstLocations.AddRange(CharacterObject.Vehicles.SelectMany(x => x.Locations));
                        foreach (Location objLocation in lstLocations)
                        {
                            TreeNode nodLocation
                                = await treSelected.DoThreadSafeFuncAsync(
                                    x => x.FindNode(objLocation.InternalId, false), token);
                            if (nodLocation == null)
                                continue;
                            if (nodLocation.Nodes.Count > 0)
                            {
                                if (nodRoot == null)
                                {
                                    nodRoot = new TreeNode
                                    {
                                        Tag = strNodeName,
                                        Text = await LanguageManager.GetStringAsync(strNodeName, token: token)
                                    };
                                    await treSelected.DoThreadSafeAsync(x => x.Nodes.Insert(0, nodRoot), token);
                                }

                                await treSelected.DoThreadSafeAsync(() =>
                                {
                                    for (int i = nodLocation.Nodes.Count - 1; i >= 0; --i)
                                    {
                                        TreeNode nodWeapon = nodLocation.Nodes[i];
                                        nodWeapon.Remove();
                                        nodRoot.Nodes.Add(nodWeapon);
                                    }
                                }, token);
                            }

                            objLocation.Remove(false);
                        }
                    }
                        break;
                }

                await treSelected.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId), token);
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        #endregion Locations

        protected async ValueTask RefreshWeapons(TreeView treWeapons, ContextMenuStrip cmsWeaponLocation, ContextMenuStrip cmsWeapon, ContextMenuStrip cmsWeaponAccessory, ContextMenuStrip cmsWeaponAccessoryGear, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treWeapons == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treWeapons.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as IHasInternalId)
                    ?.InternalId ?? string.Empty;

                TreeNode nodRoot = null;

                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await treWeapons.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await treWeapons.DoThreadSafeAsync(x =>
                        {
                            x.Nodes.Clear();
                            // Start by populating Locations.
                            foreach (Location objLocation in CharacterObject.WeaponLocations)
                            {
                                x.Nodes.Add(objLocation.CreateTreeNode(cmsWeaponLocation));
                            }
                        }, token);

                        foreach (Weapon objWeapon in CharacterObject.Weapons)
                        {
                            await AddToTree(objWeapon, -1, false);
                            objWeapon.SetupChildrenWeaponsCollectionChanged(
                                true, treWeapons, cmsWeapon, cmsWeaponAccessory,
                                cmsWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                        }

                        await treWeapons.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId),
                                                           token);
                    }
                    finally
                    {
                        await treWeapons.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    nodRoot = await treWeapons.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedWeapons", false),
                                                                     token);

                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Weapon objWeapon in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objWeapon, intNewIndex);
                                ++intNewIndex;
                                objWeapon.SetupChildrenWeaponsCollectionChanged(true, treWeapons, cmsWeapon,
                                    cmsWeaponAccessory, cmsWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (Weapon objWeapon in notifyCollectionChangedEventArgs.OldItems)
                            {
                                objWeapon.SetupChildrenWeaponsCollectionChanged(false, treWeapons);
                                await treWeapons.DoThreadSafeAsync(x => x.FindNode(objWeapon.InternalId)?.Remove(),
                                                                   token);
                            }

                            await treWeapons.DoThreadSafeAsync(() =>
                            {
                                if (nodRoot != null && nodRoot.Nodes.Count == 0)
                                {
                                    nodRoot.Remove();
                                }
                            }, token);
                        }
                            break;

                        case NotifyCollectionChangedAction.Replace:
                        {
                            foreach (Weapon objWeapon in notifyCollectionChangedEventArgs.OldItems)
                            {
                                objWeapon.SetupChildrenWeaponsCollectionChanged(false, treWeapons);
                                await treWeapons.DoThreadSafeAsync(x => x.FindNode(objWeapon.InternalId)?.Remove(),
                                                                   token);
                            }

                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Weapon objWeapon in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objWeapon, intNewIndex);
                                ++intNewIndex;
                                objWeapon.SetupChildrenWeaponsCollectionChanged(true, treWeapons, cmsWeapon,
                                    cmsWeaponAccessory, cmsWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                            }

                            await treWeapons.DoThreadSafeAsync(x =>
                            {
                                if (nodRoot != null && nodRoot.Nodes.Count == 0)
                                {
                                    nodRoot.Remove();
                                }

                                x.SelectedNode = x.FindNode(strSelectedId);
                            }, token);
                        }
                            break;

                        case NotifyCollectionChangedAction.Move:
                        {
                            await treWeapons.DoThreadSafeAsync(x =>
                            {
                                foreach (Weapon objWeapon in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    x.FindNode(objWeapon.InternalId)?.Remove();
                                }
                            }, token);

                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Weapon objWeapon in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objWeapon, intNewIndex);
                                ++intNewIndex;
                            }

                            await treWeapons.DoThreadSafeAsync(x =>
                            {
                                if (nodRoot != null && nodRoot.Nodes.Count == 0)
                                {
                                    nodRoot.Remove();
                                }

                                x.SelectedNode = x.FindNode(strSelectedId);
                            }, token);
                            break;
                        }
                    }
                }

                async ValueTask AddToTree(Weapon objWeapon, int intIndex = -1, bool blnSingleAdd = true)
                {
                    TreeNode objNode = objWeapon.CreateTreeNode(cmsWeapon, cmsWeaponAccessory, cmsWeaponAccessoryGear);
                    if (objNode == null)
                        return;
                    TreeNode nodParent = null;
                    if (objWeapon.Location != null)
                    {
                        nodParent = await treWeapons.DoThreadSafeFuncAsync(
                            x => x.FindNode(objWeapon.Location.InternalId, false), token);
                    }

                    if (nodParent == null)
                    {
                        if (nodRoot == null)
                        {
                            nodRoot = new TreeNode
                            {
                                Tag = "Node_SelectedWeapons",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedWeapons", token: token)
                            };
                            // ReSharper disable once AssignNullToNotNullAttribute
                            await treWeapons.DoThreadSafeAsync(x => x.Nodes.Insert(0, nodRoot), token);
                        }

                        nodParent = nodRoot;
                    }

                    await treWeapons.DoThreadSafeAsync(x =>
                    {
                        if (nodParent == null)
                            return;
                        if (intIndex >= 0)
                            nodParent.Nodes.Insert(intIndex, objNode);
                        else
                            nodParent.Nodes.Add(objNode);
                        nodParent.Expand();
                        if (blnSingleAdd)
                            x.SelectedNode = objNode;
                    }, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshArmor(TreeView treArmor, ContextMenuStrip cmsArmorLocation, ContextMenuStrip cmsArmor, ContextMenuStrip cmsArmorMod, ContextMenuStrip cmsArmorGear, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treArmor == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treArmor.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as IHasInternalId)
                    ?.InternalId ?? string.Empty;

                TreeNode nodRoot = null;

                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await treArmor.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await treArmor.DoThreadSafeAsync(x =>
                        {
                            x.Nodes.Clear();

                            // Start by adding Locations.
                            foreach (Location objLocation in CharacterObject.ArmorLocations)
                            {
                                x.Nodes.Add(objLocation.CreateTreeNode(cmsArmorLocation));
                            }
                        }, token);

                        // Add Armor.
                        foreach (Armor objArmor in CharacterObject.Armor)
                        {
                            await AddToTree(objArmor, -1, false);

                            async void FuncArmorModsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await RefreshArmorMods(treArmor, objArmor, cmsArmorMod, cmsArmorGear, y, token);

                            async void FuncArmorGearToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await objArmor.RefreshChildrenGears(
                                    treArmor, cmsArmorGear, () => objArmor.ArmorMods.Count, y,
                                    MakeDirtyWithCharacterUpdate, token: token);

                            await objArmor.ArmorMods.AddTaggedCollectionChangedAsync(
                                treArmor, MakeDirtyWithCharacterUpdate, token);
                            await objArmor.ArmorMods.AddTaggedCollectionChangedAsync(treArmor,
                                FuncArmorModsToAdd, token);
                            await objArmor.GearChildren.AddTaggedCollectionChangedAsync(
                                treArmor, MakeDirtyWithCharacterUpdate, token);
                            await objArmor.GearChildren.AddTaggedCollectionChangedAsync(treArmor,
                                FuncArmorGearToAdd, token);
                            foreach (Gear objGear in objArmor.GearChildren)
                                objGear.SetupChildrenGearsCollectionChanged(
                                    true, treArmor, cmsArmorGear, MakeDirtyWithCharacterUpdate);
                            foreach (ArmorMod objArmorMod in objArmor.ArmorMods)
                            {
                                async void FuncDelegateToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objArmorMod.RefreshChildrenGears(
                                        treArmor, cmsArmorGear, null, y, MakeDirtyWithCharacterUpdate, token: token);

                                await objArmorMod.GearChildren.AddTaggedCollectionChangedAsync(
                                    treArmor, MakeDirtyWithCharacterUpdate, token);
                                await objArmorMod.GearChildren.AddTaggedCollectionChangedAsync(
                                    treArmor, FuncDelegateToAdd, token);
                                foreach (Gear objGear in objArmorMod.GearChildren)
                                    objGear.SetupChildrenGearsCollectionChanged(
                                        true, treArmor, cmsArmorGear, MakeDirtyWithCharacterUpdate);
                            }
                        }

                        await treArmor.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId), token);
                    }
                    finally
                    {
                        await treArmor.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    nodRoot = await treArmor.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedArmor", false),
                                                                   token);

                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Armor objArmor in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objArmor, intNewIndex);

                                async void FuncArmorModsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await RefreshArmorMods(treArmor, objArmor, cmsArmorMod, cmsArmorGear, y, token);

                                async void FuncArmorGearToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objArmor.RefreshChildrenGears(
                                        treArmor, cmsArmorGear, () => objArmor.ArmorMods.Count, y,
                                        MakeDirtyWithCharacterUpdate, token: token);

                                await objArmor.ArmorMods.AddTaggedCollectionChangedAsync(
                                    treArmor, MakeDirtyWithCharacterUpdate, token);
                                await objArmor.ArmorMods.AddTaggedCollectionChangedAsync(treArmor,
                                    FuncArmorModsToAdd, token);
                                await objArmor.GearChildren.AddTaggedCollectionChangedAsync(
                                    treArmor, MakeDirtyWithCharacterUpdate, token);
                                await objArmor.GearChildren.AddTaggedCollectionChangedAsync(treArmor,
                                    FuncArmorGearToAdd, token);
                                foreach (Gear objGear in objArmor.GearChildren)
                                    objGear.SetupChildrenGearsCollectionChanged(
                                        true, treArmor, cmsArmorGear, MakeDirtyWithCharacterUpdate);
                                foreach (ArmorMod objArmorMod in objArmor.ArmorMods)
                                {
                                    async void FuncDelegateToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                        await objArmorMod.RefreshChildrenGears(
                                            treArmor, cmsArmorGear, null, y, MakeDirtyWithCharacterUpdate, token: token);

                                    await objArmorMod.GearChildren.AddTaggedCollectionChangedAsync(
                                        treArmor, MakeDirtyWithCharacterUpdate, token);
                                    await objArmorMod.GearChildren.AddTaggedCollectionChangedAsync(
                                        treArmor, FuncDelegateToAdd, token);
                                    foreach (Gear objGear in objArmorMod.GearChildren)
                                        objGear.SetupChildrenGearsCollectionChanged(
                                            true, treArmor, cmsArmorGear, MakeDirtyWithCharacterUpdate);
                                }

                                ++intNewIndex;
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (Armor objArmor in notifyCollectionChangedEventArgs.OldItems)
                            {
                                await objArmor.ArmorMods.RemoveTaggedCollectionChangedAsync(treArmor, token);
                                await objArmor.GearChildren.RemoveTaggedCollectionChangedAsync(treArmor, token);
                                foreach (Gear objGear in objArmor.GearChildren)
                                    objGear.SetupChildrenGearsCollectionChanged(false, treArmor);
                                foreach (ArmorMod objArmorMod in objArmor.ArmorMods)
                                {
                                    await objArmorMod.GearChildren.RemoveTaggedCollectionChangedAsync(treArmor, token);
                                    foreach (Gear objGear in objArmorMod.GearChildren)
                                        objGear.SetupChildrenGearsCollectionChanged(false, treArmor);
                                }

                                await treArmor.DoThreadSafeAsync(x => x.FindNode(objArmor.InternalId)?.Remove(),
                                                                 token);
                            }

                            await treArmor.DoThreadSafeAsync(() =>
                            {
                                if (nodRoot != null && nodRoot.Nodes.Count == 0)
                                {
                                    nodRoot.Remove();
                                }
                            }, token);

                            break;
                        }
                        case NotifyCollectionChangedAction.Replace:
                        {
                            foreach (Armor objArmor in notifyCollectionChangedEventArgs.OldItems)
                            {
                                await objArmor.ArmorMods.RemoveTaggedCollectionChangedAsync(treArmor, token);
                                await objArmor.GearChildren.RemoveTaggedCollectionChangedAsync(treArmor, token);
                                foreach (Gear objGear in objArmor.GearChildren)
                                    objGear.SetupChildrenGearsCollectionChanged(false, treArmor);
                                foreach (ArmorMod objArmorMod in objArmor.ArmorMods)
                                {
                                    await objArmorMod.GearChildren.RemoveTaggedCollectionChangedAsync(treArmor, token);
                                    foreach (Gear objGear in objArmorMod.GearChildren)
                                        objGear.SetupChildrenGearsCollectionChanged(false, treArmor);
                                }

                                await treArmor.DoThreadSafeAsync(x => x.FindNode(objArmor.InternalId)?.Remove(),
                                                                 token);
                            }

                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Armor objArmor in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objArmor, intNewIndex);

                                async void FuncArmorModsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await RefreshArmorMods(treArmor, objArmor, cmsArmorMod, cmsArmorGear, y, token);

                                async void FuncArmorGearToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objArmor.RefreshChildrenGears(
                                        treArmor, cmsArmorGear, () => objArmor.ArmorMods.Count, y,
                                        MakeDirtyWithCharacterUpdate, token: token);

                                await objArmor.ArmorMods.AddTaggedCollectionChangedAsync(
                                    treArmor, MakeDirtyWithCharacterUpdate, token);
                                await objArmor.ArmorMods.AddTaggedCollectionChangedAsync(treArmor,
                                    FuncArmorModsToAdd, token);
                                await objArmor.GearChildren.AddTaggedCollectionChangedAsync(
                                    treArmor, MakeDirtyWithCharacterUpdate, token);
                                await objArmor.GearChildren.AddTaggedCollectionChangedAsync(treArmor,
                                    FuncArmorGearToAdd, token);
                                foreach (Gear objGear in objArmor.GearChildren)
                                    objGear.SetupChildrenGearsCollectionChanged(
                                        true, treArmor, cmsArmorGear, MakeDirtyWithCharacterUpdate);
                                foreach (ArmorMod objArmorMod in objArmor.ArmorMods)
                                {
                                    async void FuncDelegateToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                        await objArmorMod.RefreshChildrenGears(
                                            treArmor, cmsArmorGear, null, y, MakeDirtyWithCharacterUpdate, token: token);

                                    await objArmorMod.GearChildren.AddTaggedCollectionChangedAsync(
                                        treArmor, MakeDirtyWithCharacterUpdate, token);
                                    await objArmorMod.GearChildren.AddTaggedCollectionChangedAsync(
                                        treArmor, FuncDelegateToAdd, token);
                                    foreach (Gear objGear in objArmorMod.GearChildren)
                                        objGear.SetupChildrenGearsCollectionChanged(
                                            true, treArmor, cmsArmorGear, MakeDirtyWithCharacterUpdate);
                                }

                                ++intNewIndex;
                            }

                            await treArmor.DoThreadSafeAsync(x =>
                            {
                                if (nodRoot != null && nodRoot.Nodes.Count == 0)
                                {
                                    nodRoot.Remove();
                                }

                                x.SelectedNode = x.FindNode(strSelectedId);
                            }, token);
                            break;
                        }
                        case NotifyCollectionChangedAction.Move:
                        {
                            await treArmor.DoThreadSafeAsync(x =>
                            {
                                foreach (Armor objArmor in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    x.FindNode(objArmor.InternalId)?.Remove();
                                }
                            }, token);

                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Armor objArmor in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objArmor, intNewIndex);
                                ++intNewIndex;
                            }

                            await treArmor.DoThreadSafeAsync(x =>
                            {
                                if (nodRoot != null && nodRoot.Nodes.Count == 0)
                                {
                                    nodRoot.Remove();
                                }

                                x.SelectedNode = x.FindNode(strSelectedId);
                            }, token);
                            break;
                        }
                    }
                }

                async ValueTask AddToTree(Armor objArmor, int intIndex = -1, bool blnSingleAdd = true)
                {
                    TreeNode objNode = objArmor.CreateTreeNode(cmsArmor, cmsArmorMod, cmsArmorGear);
                    if (objNode == null)
                        return;
                    TreeNode nodParent = null;
                    if (objArmor.Location != null)
                    {
                        nodParent = await treArmor.DoThreadSafeFuncAsync(
                            x => x.FindNode(objArmor.Location.InternalId, false), token);
                    }

                    if (nodParent == null)
                    {
                        if (nodRoot == null)
                        {
                            nodRoot = new TreeNode
                            {
                                Tag = "Node_SelectedArmor",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedArmor", token: token)
                            };
                            // ReSharper disable once AssignNullToNotNullAttribute
                            await treArmor.DoThreadSafeAsync(x => x.Nodes.Insert(0, nodRoot), token);
                        }

                        nodParent = nodRoot;
                    }

                    await treArmor.DoThreadSafeAsync(x =>
                    {
                        if (nodParent == null)
                            return;
                        if (intIndex >= 0)
                            nodParent.Nodes.Insert(intIndex, objNode);
                        else
                            nodParent.Nodes.Add(objNode);
                        nodParent.Expand();
                        if (blnSingleAdd)
                            x.SelectedNode = objNode;
                    }, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshArmorMods(TreeView treArmor, Armor objArmor, ContextMenuStrip cmsArmorMod, ContextMenuStrip cmsArmorGear, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treArmor == null || objArmor == null || notifyCollectionChangedEventArgs == null)
                return;
            TreeNode nodArmor = await treArmor.DoThreadSafeFuncAsync(x => x.FindNode(objArmor.InternalId), token);
            if (nodArmor == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                switch (notifyCollectionChangedEventArgs.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                    {
                        int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                        foreach (ArmorMod objArmorMod in notifyCollectionChangedEventArgs.NewItems)
                        {
                            await AddToTree(objArmorMod, intNewIndex);
                            await objArmorMod.GearChildren.AddTaggedCollectionChangedAsync(
                                treArmor, MakeDirtyWithCharacterUpdate, token);

                            async void FuncDelegateToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await objArmorMod.RefreshChildrenGears(treArmor, cmsArmorGear, null, y,
                                                                       MakeDirtyWithCharacterUpdate, token: token);

                            await objArmorMod.GearChildren.AddTaggedCollectionChangedAsync(treArmor,
                                FuncDelegateToAdd, token);
                            foreach (Gear objGear in objArmorMod.GearChildren)
                                objGear.SetupChildrenGearsCollectionChanged(
                                    true, treArmor, cmsArmorGear, MakeDirtyWithCharacterUpdate);
                            ++intNewIndex;
                        }

                        break;
                    }
                    case NotifyCollectionChangedAction.Remove:
                    {
                        foreach (ArmorMod objArmorMod in notifyCollectionChangedEventArgs.OldItems)
                        {
                            await objArmorMod.GearChildren.RemoveTaggedCollectionChangedAsync(treArmor, token);
                            foreach (Gear objGear in objArmorMod.GearChildren)
                                objGear.SetupChildrenGearsCollectionChanged(false, treArmor);
                            await treArmor.DoThreadSafeAsync(() => nodArmor.FindNode(objArmorMod.InternalId)?.Remove(),
                                                             token);
                        }

                        break;
                    }
                    case NotifyCollectionChangedAction.Replace:
                    {
                        string strSelectedId
                            = (await treArmor.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                                IHasInternalId)
                            ?.InternalId ?? string.Empty;
                        foreach (ArmorMod objArmorMod in notifyCollectionChangedEventArgs.OldItems)
                        {
                            await objArmorMod.GearChildren.RemoveTaggedCollectionChangedAsync(treArmor, token);
                            foreach (Gear objGear in objArmorMod.GearChildren)
                                objGear.SetupChildrenGearsCollectionChanged(false, treArmor);
                            await treArmor.DoThreadSafeAsync(() => nodArmor.FindNode(objArmorMod.InternalId)?.Remove(),
                                                             token);
                        }

                        int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                        foreach (ArmorMod objArmorMod in notifyCollectionChangedEventArgs.NewItems)
                        {
                            await AddToTree(objArmorMod, intNewIndex);
                            await objArmorMod.GearChildren.AddTaggedCollectionChangedAsync(
                                treArmor, MakeDirtyWithCharacterUpdate, token);

                            async void FuncDelegateToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await objArmorMod.RefreshChildrenGears(treArmor, cmsArmorGear, null, y,
                                                                       MakeDirtyWithCharacterUpdate, token: token);

                            await objArmorMod.GearChildren.AddTaggedCollectionChangedAsync(treArmor,
                                FuncDelegateToAdd, token);
                            foreach (Gear objGear in objArmorMod.GearChildren)
                                objGear.SetupChildrenGearsCollectionChanged(
                                    true, treArmor, cmsArmorGear, MakeDirtyWithCharacterUpdate);
                            ++intNewIndex;
                        }

                        await treArmor.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId), token);
                        break;
                    }
                    case NotifyCollectionChangedAction.Move:
                    {
                        string strSelectedId
                            = (await treArmor.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                                IHasInternalId)
                            ?.InternalId ?? string.Empty;
                        await treArmor.DoThreadSafeAsync(() =>
                        {
                            foreach (ArmorMod objArmorMod in notifyCollectionChangedEventArgs.OldItems)
                            {
                                nodArmor.FindNode(objArmorMod.InternalId)?.Remove();
                            }
                        }, token);

                        int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                        foreach (ArmorMod objArmorMod in notifyCollectionChangedEventArgs.NewItems)
                        {
                            await AddToTree(objArmorMod, intNewIndex);
                            ++intNewIndex;
                        }

                        await treArmor.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId), token);
                        break;
                    }
                    case NotifyCollectionChangedAction.Reset:
                    {
                        await treArmor.DoThreadSafeAsync(() =>
                        {
                            for (int i = nodArmor.Nodes.Count - 1; i >= 0; --i)
                            {
                                TreeNode objNode = nodArmor.Nodes[i];
                                if (objNode.Tag is ArmorMod objNodeMod && !ReferenceEquals(objNodeMod.Parent, objArmor))
                                {
                                    objNode.Remove();
                                }
                            }
                        }, token);

                        break;
                    }
                }

                Task AddToTree(ArmorMod objArmorMod, int intIndex = -1, bool blnSingleAdd = true)
                {
                    TreeNode objNode = objArmorMod.CreateTreeNode(cmsArmorMod, cmsArmorGear);
                    if (objNode == null)
                        return Task.CompletedTask;

                    return treArmor.DoThreadSafeAsync(x =>
                    {
                        if (intIndex >= 0)
                            nodArmor.Nodes.Insert(intIndex, objNode);
                        else
                            nodArmor.Nodes.Add(objNode);
                        nodArmor.Expand();
                        if (blnSingleAdd)
                            x.SelectedNode = objNode;
                    }, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshGears(TreeView treGear, ContextMenuStrip cmsGearLocation, ContextMenuStrip cmsGear, bool blnCommlinksOnly, bool blnHideLoadedAmmo, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treGear == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treGear.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as IHasInternalId)
                    ?.InternalId ?? string.Empty;

                TreeNode nodRoot = null;

                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await treGear.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await treGear.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                        // Start by populating Locations.
                        foreach (Location objLocation in CharacterObject.GearLocations)
                        {
                            await treGear.DoThreadSafeAsync(
                                x => x.Nodes.Add(objLocation.CreateTreeNode(cmsGearLocation)), token);
                        }

                        // Add Gear.
                        foreach (Gear objGear in CharacterObject.Gear)
                        {
                            await AddToTree(objGear, -1, false);
                            objGear.SetupChildrenGearsCollectionChanged(
                                true, treGear, cmsGear, MakeDirtyWithCharacterUpdate);
                        }

                        await treGear.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId), token);
                    }
                    finally
                    {
                        await treGear.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    nodRoot = await treGear.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedGear", false),
                                                                  token);

                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Gear objGear in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objGear, intNewIndex);
                                objGear.SetupChildrenGearsCollectionChanged(
                                    true, treGear, cmsGear, MakeDirtyWithCharacterUpdate);
                                ++intNewIndex;
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (Gear objGear in notifyCollectionChangedEventArgs.OldItems)
                            {
                                objGear.SetupChildrenGearsCollectionChanged(false, treGear);
                                await treGear.DoThreadSafeAsync(x => x.FindNodeByTag(objGear)?.Remove(), token);
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Replace:
                        {
                            foreach (Gear objGear in notifyCollectionChangedEventArgs.OldItems)
                            {
                                objGear.SetupChildrenGearsCollectionChanged(false, treGear);
                                await treGear.DoThreadSafeAsync(x => x.FindNodeByTag(objGear)?.Remove(), token);
                            }

                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Gear objGear in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objGear, intNewIndex);

                                async void FuncGearToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objGear.RefreshChildrenGears(treGear, cmsGear, null, y,
                                                                       MakeDirtyWithCharacterUpdate, token: token);

                                await objGear.Children.AddTaggedCollectionChangedAsync(
                                    treGear, MakeDirtyWithCharacterUpdate, token);
                                await objGear.Children.AddTaggedCollectionChangedAsync(
                                    treGear, FuncGearToAdd, token);
                                objGear.SetupChildrenGearsCollectionChanged(
                                    true, treGear, cmsGear, MakeDirtyWithCharacterUpdate);
                                ++intNewIndex;
                            }

                            await treGear.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId),
                                                            token);
                        }
                            break;

                        case NotifyCollectionChangedAction.Move:
                        {
                            await treGear.DoThreadSafeAsync(x =>
                            {
                                foreach (Gear objGear in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    x.FindNodeByTag(objGear)?.Remove();
                                }
                            }, token);

                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Gear objGear in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objGear, intNewIndex);
                                ++intNewIndex;
                            }

                            await treGear.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId),
                                                            token);
                        }
                            break;
                    }
                }

                async ValueTask AddToTree(Gear objGear, int intIndex = -1, bool blnSingleAdd = true)
                {
                    if (blnCommlinksOnly && !objGear.IsCommlink)
                        return;

                    if (blnHideLoadedAmmo && objGear.LoadedIntoClip != null)
                        return;

                    TreeNode objNode = objGear.CreateTreeNode(cmsGear);
                    if (objNode == null)
                        return;
                    TreeNode nodParent = null;
                    if (objGear.Location != null)
                    {
                        nodParent = await treGear.DoThreadSafeFuncAsync(x => x.FindNodeByTag(objGear.Location, false),
                                                                        token);
                    }

                    if (nodParent == null)
                    {
                        if (nodRoot == null)
                        {
                            nodRoot = new TreeNode
                            {
                                Tag = "Node_SelectedGear",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedGear", token: token)
                            };
                            // ReSharper disable once AssignNullToNotNullAttribute
                            await treGear.DoThreadSafeAsync(x => x.Nodes.Insert(0, nodRoot), token);
                        }

                        nodParent = nodRoot;
                    }

                    await treGear.DoThreadSafeAsync(x =>
                    {
                        if (nodParent == null)
                            return;
                        if (intIndex >= 0)
                            nodParent.Nodes.Insert(intIndex, objNode);
                        else
                            nodParent.Nodes.Add(objNode);
                        nodParent.Expand();
                        if (blnSingleAdd)
                            x.SelectedNode = objNode;
                    }, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshDrugs(TreeView treGear, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treGear == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treGear.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as IHasInternalId)
                    ?.InternalId ?? string.Empty;

                TreeNode nodRoot = null;

                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await treGear.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await treGear.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                        // Add Gear.
                        foreach (Drug d in CharacterObject.Drugs)
                        {
                            await AddToTree(d, -1, false);
                        }

                        await treGear.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId), token);
                    }
                    finally
                    {
                        await treGear.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    nodRoot = await treGear.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedDrugs", false),
                                                                  token);

                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Drug d in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(d, intNewIndex);
                                ++intNewIndex;
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Remove:
                        {
                            await treGear.DoThreadSafeAsync(x =>
                            {
                                foreach (Drug d in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    x.FindNodeByTag(d)?.Remove();
                                }
                            }, token);
                        }
                            break;

                        case NotifyCollectionChangedAction.Replace:
                        case NotifyCollectionChangedAction.Move:
                        {
                            await treGear.DoThreadSafeAsync(x =>
                            {
                                foreach (Drug d in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    x.FindNodeByTag(d)?.Remove();
                                }
                            }, token);

                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Drug d in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(d, intNewIndex);
                                ++intNewIndex;
                            }

                            await treGear.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId),
                                                            token);
                        }
                            break;
                    }
                }

                async ValueTask AddToTree(Drug objGear, int intIndex = -1, bool blnSingleAdd = true)
                {
                    TreeNode objNode = objGear.CreateTreeNode();
                    if (objNode == null)
                        return;
                    if (nodRoot == null)
                    {
                        nodRoot = new TreeNode
                        {
                            Tag = "Node_SelectedDrugs",
                            Text = await LanguageManager.GetStringAsync("Node_SelectedDrugs", token: token)
                        };
                        // ReSharper disable once AssignNullToNotNullAttribute
                        await treGear.DoThreadSafeAsync(x => x.Nodes.Insert(0, nodRoot), token);
                    }

                    await treGear.DoThreadSafeAsync(x =>
                    {
                        if (nodRoot == null)
                            return;
                        if (intIndex >= 0)
                            nodRoot.Nodes.Insert(intIndex, objNode);
                        else
                            nodRoot.Nodes.Add(objNode);
                        nodRoot.Expand();
                        if (blnSingleAdd)
                            x.SelectedNode = objNode;
                    }, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshCyberware(TreeView treCyberware, ContextMenuStrip cmsCyberware, ContextMenuStrip cmsCyberwareGear, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treCyberware == null)
                return;

            TreeNode objCyberwareRoot = null;
            TreeNode objBiowareRoot = null;
            TreeNode objModularRoot = null;
            TreeNode objHoleNode = null;
            TreeNode objAntiHoleNode = null;

            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treCyberware.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                        IHasInternalId)?.InternalId ?? string.Empty;

                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await treCyberware.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await treCyberware.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                        foreach (Cyberware objCyberware in CharacterObject.Cyberware)
                        {
                            await AddToTree(objCyberware, false);
                            objCyberware.SetupChildrenCyberwareCollectionChanged(true, treCyberware, cmsCyberware,
                                cmsCyberwareGear, MakeDirtyWithCharacterUpdate);
                        }

                        await treCyberware.DoThreadSafeAsync(x => x.SortCustomAlphabetically(strSelectedId),
                                                             token);
                    }
                    finally
                    {
                        await treCyberware.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    await treCyberware.DoThreadSafeAsync(x =>
                    {
                        objCyberwareRoot = x.FindNode("Node_SelectedCyberware", false);
                        objBiowareRoot = x.FindNode("Node_SelectedBioware", false);
                        objModularRoot = x.FindNode("Node_UnequippedModularCyberware", false);
                        objHoleNode = x.FindNode(
                            Cyberware.EssenceHoleGUID.ToString("D", GlobalSettings.InvariantCultureInfo), false);
                        objAntiHoleNode
                            = x.FindNode(
                                Cyberware.EssenceAntiHoleGUID.ToString("D", GlobalSettings.InvariantCultureInfo),
                                false);
                    }, token);
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (Cyberware objCyberware in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objCyberware);
                                objCyberware.SetupChildrenCyberwareCollectionChanged(true, treCyberware, cmsCyberware,
                                    cmsCyberwareGear, MakeDirtyWithCharacterUpdate);
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (Cyberware objCyberware in notifyCollectionChangedEventArgs.OldItems)
                            {
                                objCyberware.SetupChildrenCyberwareCollectionChanged(false, treCyberware);
                                await treCyberware.DoThreadSafeAsync(x =>
                                {
                                    TreeNode objNode = x.FindNodeByTag(objCyberware);
                                    if (objNode != null)
                                    {
                                        TreeNode objParent = objNode.Parent;
                                        objNode.Remove();
                                        if (objParent != null && objParent.Level == 0 && objParent.Nodes.Count == 0)
                                            objParent.Remove();
                                    }
                                }, token);
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Replace:
                        {
                            List<TreeNode> lstOldParentNodes =
                                new List<TreeNode>(notifyCollectionChangedEventArgs.OldItems.Count);

                            foreach (Cyberware objCyberware in notifyCollectionChangedEventArgs.OldItems)
                            {
                                objCyberware.SetupChildrenCyberwareCollectionChanged(false, treCyberware);
                                await treCyberware.DoThreadSafeAsync(x =>
                                {
                                    TreeNode objNode = x.FindNodeByTag(objCyberware);
                                    if (objNode != null)
                                    {
                                        TreeNode objParent = objNode.Parent;
                                        objNode.Remove();
                                        if (objParent != null && objParent.Level == 0)
                                            lstOldParentNodes.Add(objParent);
                                    }
                                }, token);
                            }

                            foreach (Cyberware objCyberware in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objCyberware);
                                objCyberware.SetupChildrenCyberwareCollectionChanged(true, treCyberware, cmsCyberware,
                                    cmsCyberwareGear, MakeDirtyWithCharacterUpdate);
                            }

                            await treCyberware.DoThreadSafeAsync(x =>
                            {
                                foreach (TreeNode objOldParent in lstOldParentNodes)
                                {
                                    if (objOldParent.Nodes.Count == 0)
                                        objOldParent.Remove();
                                }

                                x.SelectedNode = x.FindNode(strSelectedId);
                            }, token);
                        }
                            break;
                    }
                }

                async ValueTask AddToTree(Cyberware objCyberware, bool blnSingleAdd = true)
                {
                    if (objCyberware.SourceID == Cyberware.EssenceHoleGUID)
                    {
                        await treCyberware.DoThreadSafeAsync(x =>
                        {
                            if (objHoleNode == null)
                            {
                                objHoleNode = objCyberware.CreateTreeNode(null, null);
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Insert(3, objHoleNode);
                            }

                            if (blnSingleAdd)
                                x.SelectedNode = objHoleNode;
                        }, token);
                        return;
                    }

                    if (objCyberware.SourceID == Cyberware.EssenceAntiHoleGUID)
                    {
                        await treCyberware.DoThreadSafeAsync(x =>
                        {
                            if (objAntiHoleNode == null)
                            {
                                objAntiHoleNode = objCyberware.CreateTreeNode(null, null);
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Insert(3, objAntiHoleNode);
                            }

                            if (blnSingleAdd)
                                x.SelectedNode = objAntiHoleNode;
                        }, token);
                        return;
                    }

                    TreeNode objNode = objCyberware.CreateTreeNode(cmsCyberware, cmsCyberwareGear);
                    if (objNode == null)
                        return;

                    TreeNode nodParent = null;
                    switch (objCyberware.SourceType)
                    {
                        case Improvement.ImprovementSource.Cyberware when objCyberware.IsModularCurrentlyEquipped:
                        {
                            if (objCyberwareRoot == null)
                            {
                                objCyberwareRoot = new TreeNode
                                {
                                    Tag = "Node_SelectedCyberware",
                                    Text = await LanguageManager.GetStringAsync("Node_SelectedCyberware", token: token)
                                };
                                await treCyberware.DoThreadSafeAsync(x =>
                                {
                                    // ReSharper disable once AssignNullToNotNullAttribute
                                    x.Nodes.Insert(0, objCyberwareRoot);
                                    objCyberwareRoot.Expand();
                                }, token);
                            }

                            nodParent = objCyberwareRoot;
                            break;
                        }
                        case Improvement.ImprovementSource.Cyberware:
                        {
                            if (objModularRoot == null)
                            {
                                objModularRoot = new TreeNode
                                {
                                    Tag = "Node_UnequippedModularCyberware",
                                    Text = await LanguageManager.GetStringAsync("Node_UnequippedModularCyberware", token: token)
                                };
                                int intIndex = 0;
                                if (objBiowareRoot != null || objCyberwareRoot != null)
                                    intIndex = objBiowareRoot != null && objCyberwareRoot != null ? 2 : 1;
                                await treCyberware.DoThreadSafeAsync(x =>
                                {
                                    // ReSharper disable once AssignNullToNotNullAttribute
                                    x.Nodes.Insert(intIndex, objModularRoot);
                                    objModularRoot.Expand();
                                }, token);
                            }

                            nodParent = objModularRoot;
                            break;
                        }
                        case Improvement.ImprovementSource.Bioware:
                        {
                            if (objBiowareRoot == null)
                            {
                                objBiowareRoot = new TreeNode
                                {
                                    Tag = "Node_SelectedBioware",
                                    Text = await LanguageManager.GetStringAsync("Node_SelectedBioware", token: token)
                                };
                                await treCyberware.DoThreadSafeAsync(x =>
                                {
                                    // ReSharper disable once AssignNullToNotNullAttribute
                                    x.Nodes.Insert(objCyberwareRoot == null ? 0 : 1, objBiowareRoot);
                                    objBiowareRoot.Expand();
                                }, token);
                            }

                            nodParent = objBiowareRoot;
                            break;
                        }
                    }

                    if (nodParent != null)
                    {
                        await treCyberware.DoThreadSafeAsync(x =>
                        {
                            if (blnSingleAdd)
                            {
                                TreeNodeCollection lstParentNodeChildren = nodParent.Nodes;
                                int intNodesCount = lstParentNodeChildren.Count;
                                int intTargetIndex = 0;
                                for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                                {
                                    if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode)
                                        >= 0)
                                    {
                                        break;
                                    }
                                }

                                lstParentNodeChildren.Insert(intTargetIndex, objNode);
                                x.SelectedNode = objNode;
                            }
                            else
                                nodParent.Nodes.Add(objNode);
                        }, token);
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshVehicles(TreeView treVehicles, ContextMenuStrip cmsVehicleLocation, ContextMenuStrip cmsVehicle, ContextMenuStrip cmsVehicleWeapon, ContextMenuStrip cmsVehicleWeaponAccessory, ContextMenuStrip cmsVehicleWeaponAccessoryGear, ContextMenuStrip cmsVehicleGear, ContextMenuStrip cmsVehicleWeaponMount, ContextMenuStrip cmsCyberware, ContextMenuStrip cmsCyberwareGear, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treVehicles == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treVehicles.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag,
                                                               token) as IHasInternalId)?.InternalId
                      ?? string.Empty;

                TreeNode nodRoot = null;

                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await treVehicles.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await treVehicles.DoThreadSafeAsync(x =>
                        {
                            x.Nodes.Clear();

                            // Start by populating Locations.
                            foreach (Location objLocation in CharacterObject.VehicleLocations)
                            {
                                x.Nodes.Add(objLocation.CreateTreeNode(cmsVehicleLocation));
                            }
                        }, token);

                        // Add Vehicles.
                        foreach (Vehicle objVehicle in CharacterObject.Vehicles)
                        {
                            await AddToTree(objVehicle, -1, false);

                            async void FuncVehicleModsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await objVehicle.RefreshVehicleMods(
                                    treVehicles, cmsVehicle, cmsCyberware, cmsCyberwareGear, cmsVehicleWeapon,
                                    cmsVehicleWeaponAccessory, cmsVehicleWeaponAccessoryGear, null, y,
                                    MakeDirtyWithCharacterUpdate, token: token);

                            async void
                                FuncVehicleWeaponMountsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await objVehicle.RefreshVehicleWeaponMounts(
                                    treVehicles, cmsVehicleWeaponMount, cmsVehicleWeapon,
                                    cmsVehicleWeaponAccessory, cmsVehicleWeaponAccessoryGear, cmsCyberware,
                                    cmsCyberwareGear, cmsVehicle, () => objVehicle.Mods.Count, y,
                                    MakeDirtyWithCharacterUpdate, token: token);

                            async void FuncVehicleWeaponsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await objVehicle.RefreshChildrenWeapons(
                                    treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                    cmsVehicleWeaponAccessoryGear,
                                    () => objVehicle.Mods.Count + (objVehicle.WeaponMounts.Count > 0 ? 1 : 0),
                                    y, MakeDirtyWithCharacterUpdate, token: token);

                            await objVehicle.Mods.AddTaggedCollectionChangedAsync(
                                treVehicles, MakeDirtyWithCharacterUpdate, token);
                            await objVehicle.Mods.AddTaggedCollectionChangedAsync(
                                treVehicles, FuncVehicleModsToAdd, token);
                            await objVehicle.WeaponMounts.AddTaggedCollectionChangedAsync(
                                treVehicles, MakeDirtyWithCharacterUpdate, token);
                            await objVehicle.WeaponMounts.AddTaggedCollectionChangedAsync(
                                treVehicles, FuncVehicleWeaponMountsToAdd, token);
                            await objVehicle.Weapons.AddTaggedCollectionChangedAsync(
                                treVehicles, MakeDirtyWithCharacterUpdate, token);
                            await objVehicle.Weapons.AddTaggedCollectionChangedAsync(
                                treVehicles, FuncVehicleWeaponsToAdd, token);
                            foreach (VehicleMod objMod in objVehicle.Mods)
                            {
                                async void FuncVehicleModCyberwareToAdd(
                                    object x, NotifyCollectionChangedEventArgs y) =>
                                    await objMod.RefreshChildrenCyberware(
                                        treVehicles, cmsCyberware, cmsCyberwareGear, null, y,
                                        MakeDirtyWithCharacterUpdate, token: token);

                                async void FuncVehicleModWeaponsToAdd(
                                    object x, NotifyCollectionChangedEventArgs y) =>
                                    await objMod.RefreshChildrenWeapons(
                                        treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                        cmsVehicleWeaponAccessoryGear, () => objMod.Cyberware.Count, y,
                                        MakeDirtyWithCharacterUpdate, token: token);

                                await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleModCyberwareToAdd, token);
                                foreach (Cyberware objCyberware in objMod.Cyberware)
                                    objCyberware.SetupChildrenCyberwareCollectionChanged(true, treVehicles,
                                        cmsCyberware, cmsCyberwareGear, MakeDirtyWithCharacterUpdate);
                                await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleModWeaponsToAdd, token);
                                foreach (Weapon objWeapon in objMod.Weapons)
                                    objWeapon.SetupChildrenWeaponsCollectionChanged(true, treVehicles,
                                        cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                        cmsVehicleWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                            }

                            foreach (WeaponMount objMount in objVehicle.WeaponMounts)
                            {
                                async void FuncWeaponMountVehicleModToAdd(
                                    object x, NotifyCollectionChangedEventArgs y) =>
                                    await objMount.RefreshVehicleMods(treVehicles, cmsVehicle, cmsCyberware,
                                                                      cmsCyberwareGear, cmsVehicleWeapon,
                                                                      cmsVehicleWeaponAccessory,
                                                                      cmsVehicleWeaponAccessoryGear, null, y,
                                                                      MakeDirtyWithCharacterUpdate, token: token);

                                async void FuncWeaponMountWeaponsToAdd(
                                    object x, NotifyCollectionChangedEventArgs y) =>
                                    await objMount.RefreshChildrenWeapons(
                                        treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                        cmsVehicleWeaponAccessoryGear, () => objMount.Mods.Count, y,
                                        MakeDirtyWithCharacterUpdate, token: token);

                                await objMount.Mods.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objMount.Mods.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncWeaponMountVehicleModToAdd, token);
                                await objMount.Weapons.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objMount.Weapons.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncWeaponMountWeaponsToAdd, token);
                                foreach (Weapon objWeapon in objMount.Weapons)
                                    objWeapon.SetupChildrenWeaponsCollectionChanged(true, treVehicles,
                                        cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                        cmsVehicleWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                                foreach (VehicleMod objMod in objMount.Mods)
                                {
                                    async void FuncWeaponMountVehicleModCyberwareToAdd(
                                        object x, NotifyCollectionChangedEventArgs y) =>
                                        await objMod.RefreshChildrenCyberware(
                                            treVehicles, cmsCyberware, cmsCyberwareGear, null, y,
                                            MakeDirtyWithCharacterUpdate, token: token);

                                    async void FuncWeaponMountVehicleModWeaponsToAdd(
                                        object x, NotifyCollectionChangedEventArgs y) =>
                                        await objMod.RefreshChildrenWeapons(
                                            treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                            cmsVehicleWeaponAccessoryGear, () => objMod.Cyberware.Count, y,
                                            MakeDirtyWithCharacterUpdate, token: token);

                                    await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                        treVehicles, MakeDirtyWithCharacterUpdate, token);
                                    await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                        treVehicles, FuncWeaponMountVehicleModCyberwareToAdd, token);
                                    foreach (Cyberware objCyberware in objMod.Cyberware)
                                        objCyberware.SetupChildrenCyberwareCollectionChanged(true, treVehicles,
                                            cmsCyberware, cmsCyberwareGear, MakeDirtyWithCharacterUpdate);
                                    await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                        treVehicles, MakeDirtyWithCharacterUpdate, token);
                                    await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                        treVehicles, FuncWeaponMountVehicleModWeaponsToAdd, token);
                                    foreach (Weapon objWeapon in objMod.Weapons)
                                        objWeapon.SetupChildrenWeaponsCollectionChanged(true, treVehicles,
                                            cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                            cmsVehicleWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                                }
                            }

                            foreach (Weapon objWeapon in objVehicle.Weapons)
                                objWeapon.SetupChildrenWeaponsCollectionChanged(
                                    true, treVehicles, cmsVehicleWeapon,
                                    cmsVehicleWeaponAccessory, cmsVehicleWeaponAccessoryGear,
                                    MakeDirtyWithCharacterUpdate);

                            async void FuncVehicleGearToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await objVehicle.RefreshChildrenGears(
                                    treVehicles, cmsVehicleGear,
                                    () => objVehicle.Mods.Count + objVehicle.Weapons.Count
                                                                + (objVehicle.WeaponMounts.Count > 0 ? 1 : 0),
                                    y, MakeDirtyWithCharacterUpdate, token: token);

                            async void FuncVehicleLocationsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await RefreshLocationsInVehicle(treVehicles, objVehicle, cmsVehicleLocation,
                                                                () => objVehicle.Mods.Count + objVehicle.Weapons.Count
                                                                    + (objVehicle.WeaponMounts.Count > 0 ? 1 : 0)
                                                                    + objVehicle.GearChildren.Count(
                                                                        z => z.Location == null), y, token);

                            await objVehicle.GearChildren.AddTaggedCollectionChangedAsync(
                                treVehicles, MakeDirtyWithCharacterUpdate, token);
                            await objVehicle.GearChildren.AddTaggedCollectionChangedAsync(
                                treVehicles, FuncVehicleGearToAdd, token);
                            foreach (Gear objGear in objVehicle.GearChildren)
                                objGear.SetupChildrenGearsCollectionChanged(
                                    true, treVehicles, cmsVehicleGear, MakeDirtyWithCharacterUpdate);
                            await objVehicle.Locations.AddTaggedCollectionChangedAsync(
                                treVehicles, MakeDirtyWithCharacterUpdate, token);
                            await objVehicle.Locations.AddTaggedCollectionChangedAsync(
                                treVehicles, FuncVehicleLocationsToAdd, token);
                        }

                        await treVehicles.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId),
                                                            token);
                    }
                    finally
                    {
                        await treVehicles.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    nodRoot = await treVehicles.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedVehicles", false),
                                                                      token);

                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Vehicle objVehicle in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objVehicle, intNewIndex);

                                async void FuncVehicleModsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objVehicle.RefreshVehicleMods(
                                        treVehicles, cmsVehicle, cmsCyberware, cmsCyberwareGear, cmsVehicleWeapon,
                                        cmsVehicleWeaponAccessory, cmsVehicleWeaponAccessoryGear, null, y,
                                        MakeDirtyWithCharacterUpdate, token: token);

                                async void
                                    FuncVehicleWeaponMountsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objVehicle.RefreshVehicleWeaponMounts(
                                        treVehicles, cmsVehicleWeaponMount, cmsVehicleWeapon,
                                        cmsVehicleWeaponAccessory, cmsVehicleWeaponAccessoryGear, cmsCyberware,
                                        cmsCyberwareGear, cmsVehicle, () => objVehicle.Mods.Count, y,
                                        MakeDirtyWithCharacterUpdate, token: token);

                                async void FuncVehicleWeaponsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objVehicle.RefreshChildrenWeapons(
                                        treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                        cmsVehicleWeaponAccessoryGear,
                                        () => objVehicle.Mods.Count + (objVehicle.WeaponMounts.Count > 0 ? 1 : 0),
                                        y, MakeDirtyWithCharacterUpdate, token: token);

                                await objVehicle.Mods.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objVehicle.Mods.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleModsToAdd, token);
                                await objVehicle.WeaponMounts.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objVehicle.WeaponMounts.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleWeaponMountsToAdd, token);
                                await objVehicle.Weapons.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objVehicle.Weapons.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleWeaponsToAdd, token);
                                foreach (VehicleMod objMod in objVehicle.Mods)
                                {
                                    async void FuncVehicleModCyberwareToAdd(
                                        object x, NotifyCollectionChangedEventArgs y) =>
                                        await objMod.RefreshChildrenCyberware(
                                            treVehicles, cmsCyberware, cmsCyberwareGear, null, y,
                                            MakeDirtyWithCharacterUpdate, token: token);

                                    async void FuncVehicleModWeaponsToAdd(
                                        object x, NotifyCollectionChangedEventArgs y) =>
                                        await objMod.RefreshChildrenWeapons(
                                            treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                            cmsVehicleWeaponAccessoryGear, () => objMod.Cyberware.Count, y,
                                            MakeDirtyWithCharacterUpdate, token: token);

                                    await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                        treVehicles, MakeDirtyWithCharacterUpdate, token);
                                    await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                        treVehicles, FuncVehicleModCyberwareToAdd, token);
                                    foreach (Cyberware objCyberware in objMod.Cyberware)
                                        objCyberware.SetupChildrenCyberwareCollectionChanged(true, treVehicles,
                                            cmsCyberware, cmsCyberwareGear, MakeDirtyWithCharacterUpdate);
                                    await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                        treVehicles, MakeDirtyWithCharacterUpdate, token);
                                    await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                        treVehicles, FuncVehicleModWeaponsToAdd, token);
                                    foreach (Weapon objWeapon in objMod.Weapons)
                                        objWeapon.SetupChildrenWeaponsCollectionChanged(true, treVehicles,
                                            cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                            cmsVehicleWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                                }

                                foreach (WeaponMount objMount in objVehicle.WeaponMounts)
                                {
                                    async void FuncWeaponMountVehicleModToAdd(
                                        object x, NotifyCollectionChangedEventArgs y) =>
                                        await objMount.RefreshVehicleMods(treVehicles, cmsVehicle, cmsCyberware,
                                                                          cmsCyberwareGear, cmsVehicleWeapon,
                                                                          cmsVehicleWeaponAccessory,
                                                                          cmsVehicleWeaponAccessoryGear, null, y,
                                                                          MakeDirtyWithCharacterUpdate, token: token);

                                    async void FuncWeaponMountWeaponsToAdd(
                                        object x, NotifyCollectionChangedEventArgs y) =>
                                        await objMount.RefreshChildrenWeapons(
                                            treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                            cmsVehicleWeaponAccessoryGear, () => objMount.Mods.Count, y,
                                            MakeDirtyWithCharacterUpdate, token: token);

                                    await objMount.Mods.AddTaggedCollectionChangedAsync(
                                        treVehicles, MakeDirtyWithCharacterUpdate, token);
                                    await objMount.Mods.AddTaggedCollectionChangedAsync(
                                        treVehicles, FuncWeaponMountVehicleModToAdd, token);
                                    await objMount.Weapons.AddTaggedCollectionChangedAsync(
                                        treVehicles, MakeDirtyWithCharacterUpdate, token);
                                    await objMount.Weapons.AddTaggedCollectionChangedAsync(
                                        treVehicles, FuncWeaponMountWeaponsToAdd, token);
                                    foreach (Weapon objWeapon in objMount.Weapons)
                                        objWeapon.SetupChildrenWeaponsCollectionChanged(true, treVehicles,
                                            cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                            cmsVehicleWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                                    foreach (VehicleMod objMod in objMount.Mods)
                                    {
                                        async void FuncWeaponMountVehicleModCyberwareToAdd(
                                            object x, NotifyCollectionChangedEventArgs y) =>
                                            await objMod.RefreshChildrenCyberware(
                                                treVehicles, cmsCyberware, cmsCyberwareGear, null, y,
                                                MakeDirtyWithCharacterUpdate, token: token);

                                        async void FuncWeaponMountVehicleModWeaponsToAdd(
                                            object x, NotifyCollectionChangedEventArgs y) =>
                                            await objMod.RefreshChildrenWeapons(
                                                treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                                cmsVehicleWeaponAccessoryGear, () => objMod.Cyberware.Count, y,
                                                MakeDirtyWithCharacterUpdate, token: token);

                                        await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                            treVehicles, MakeDirtyWithCharacterUpdate, token);
                                        await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                            treVehicles, FuncWeaponMountVehicleModCyberwareToAdd, token);
                                        foreach (Cyberware objCyberware in objMod.Cyberware)
                                            objCyberware.SetupChildrenCyberwareCollectionChanged(true, treVehicles,
                                                cmsCyberware, cmsCyberwareGear, MakeDirtyWithCharacterUpdate);
                                        await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                            treVehicles, MakeDirtyWithCharacterUpdate, token);
                                        await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                            treVehicles, FuncWeaponMountVehicleModWeaponsToAdd, token);
                                        foreach (Weapon objWeapon in objMod.Weapons)
                                            objWeapon.SetupChildrenWeaponsCollectionChanged(true, treVehicles,
                                                cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                                cmsVehicleWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                                    }
                                }

                                foreach (Weapon objWeapon in objVehicle.Weapons)
                                    objWeapon.SetupChildrenWeaponsCollectionChanged(
                                        true, treVehicles, cmsVehicleWeapon,
                                        cmsVehicleWeaponAccessory, cmsVehicleWeaponAccessoryGear,
                                        MakeDirtyWithCharacterUpdate);

                                async void FuncVehicleGearToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objVehicle.RefreshChildrenGears(
                                        treVehicles, cmsVehicleGear,
                                        () => objVehicle.Mods.Count + objVehicle.Weapons.Count
                                                                    + (objVehicle.WeaponMounts.Count > 0 ? 1 : 0),
                                        y, MakeDirtyWithCharacterUpdate, token: token);

                                async void FuncVehicleLocationsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await RefreshLocationsInVehicle(treVehicles, objVehicle, cmsVehicleLocation,
                                                                    () => objVehicle.Mods.Count
                                                                          + objVehicle.Weapons.Count
                                                                          + (objVehicle.WeaponMounts.Count > 0 ? 1 : 0)
                                                                          + objVehicle.GearChildren.Count(
                                                                              z => z.Location == null), y, token);

                                await objVehicle.GearChildren.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objVehicle.GearChildren.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleGearToAdd, token);
                                foreach (Gear objGear in objVehicle.GearChildren)
                                    objGear.SetupChildrenGearsCollectionChanged(
                                        true, treVehicles, cmsVehicleGear, MakeDirtyWithCharacterUpdate);
                                await objVehicle.Locations.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objVehicle.Locations.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleLocationsToAdd, token);

                                ++intNewIndex;
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (Vehicle objVehicle in notifyCollectionChangedEventArgs.OldItems)
                            {
                                await objVehicle.Mods.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                await objVehicle.WeaponMounts.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                await objVehicle.Weapons.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                foreach (VehicleMod objMod in objVehicle.Mods)
                                {
                                    await objMod.Cyberware.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                    foreach (Cyberware objCyberware in objMod.Cyberware)
                                        objCyberware.SetupChildrenCyberwareCollectionChanged(false, treVehicles);
                                    await objMod.Weapons.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                    foreach (Weapon objWeapon in objMod.Weapons)
                                        objWeapon.SetupChildrenWeaponsCollectionChanged(false, treVehicles);
                                }

                                foreach (WeaponMount objMount in objVehicle.WeaponMounts)
                                {
                                    await objMount.Mods.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                    await objMount.Weapons.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                    foreach (Weapon objWeapon in objMount.Weapons)
                                        objWeapon.SetupChildrenWeaponsCollectionChanged(false, treVehicles);
                                    foreach (VehicleMod objMod in objMount.Mods)
                                    {
                                        await objMod.Cyberware.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                        foreach (Cyberware objCyberware in objMod.Cyberware)
                                            objCyberware.SetupChildrenCyberwareCollectionChanged(false, treVehicles);
                                        await objMod.Weapons.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                        foreach (Weapon objWeapon in objMod.Weapons)
                                            objWeapon.SetupChildrenWeaponsCollectionChanged(false, treVehicles);
                                    }
                                }

                                foreach (Weapon objWeapon in objVehicle.Weapons)
                                    objWeapon.SetupChildrenWeaponsCollectionChanged(false, treVehicles);
                                await objVehicle.GearChildren.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                foreach (Gear objGear in objVehicle.GearChildren)
                                    objGear.SetupChildrenGearsCollectionChanged(false, treVehicles);
                                await objVehicle.Locations.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                await treVehicles.DoThreadSafeAsync(x => x.FindNodeByTag(objVehicle)?.Remove(),
                                                                    token);
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Replace:
                        {
                            foreach (Vehicle objVehicle in notifyCollectionChangedEventArgs.OldItems)
                            {
                                await objVehicle.Mods.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                await objVehicle.WeaponMounts.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                await objVehicle.Weapons.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                foreach (VehicleMod objMod in objVehicle.Mods)
                                {
                                    await objMod.Cyberware.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                    foreach (Cyberware objCyberware in objMod.Cyberware)
                                        objCyberware.SetupChildrenCyberwareCollectionChanged(false, treVehicles);
                                    await objMod.Weapons.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                    foreach (Weapon objWeapon in objMod.Weapons)
                                        objWeapon.SetupChildrenWeaponsCollectionChanged(false, treVehicles);
                                }

                                foreach (WeaponMount objMount in objVehicle.WeaponMounts)
                                {
                                    await objMount.Mods.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                    await objMount.Weapons.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                    foreach (Weapon objWeapon in objMount.Weapons)
                                        objWeapon.SetupChildrenWeaponsCollectionChanged(false, treVehicles);
                                    foreach (VehicleMod objMod in objMount.Mods)
                                    {
                                        await objMod.Cyberware.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                        foreach (Cyberware objCyberware in objMod.Cyberware)
                                            objCyberware.SetupChildrenCyberwareCollectionChanged(false, treVehicles);
                                        await objMod.Weapons.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                        foreach (Weapon objWeapon in objMod.Weapons)
                                            objWeapon.SetupChildrenWeaponsCollectionChanged(false, treVehicles);
                                    }
                                }

                                foreach (Weapon objWeapon in objVehicle.Weapons)
                                    objWeapon.SetupChildrenWeaponsCollectionChanged(false, treVehicles);
                                await objVehicle.GearChildren.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                foreach (Gear objGear in objVehicle.GearChildren)
                                    objGear.SetupChildrenGearsCollectionChanged(false, treVehicles);
                                await objVehicle.Locations.RemoveTaggedCollectionChangedAsync(treVehicles, token);
                                await treVehicles.DoThreadSafeAsync(x => x.FindNodeByTag(objVehicle)?.Remove(),
                                                                    token);
                            }

                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Vehicle objVehicle in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objVehicle, intNewIndex);

                                async void FuncVehicleModsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objVehicle.RefreshVehicleMods(
                                        treVehicles, cmsVehicle, cmsCyberware, cmsCyberwareGear, cmsVehicleWeapon,
                                        cmsVehicleWeaponAccessory, cmsVehicleWeaponAccessoryGear, null, y,
                                        MakeDirtyWithCharacterUpdate, token: token);

                                async void
                                    FuncVehicleWeaponMountsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objVehicle.RefreshVehicleWeaponMounts(
                                        treVehicles, cmsVehicleWeaponMount, cmsVehicleWeapon,
                                        cmsVehicleWeaponAccessory, cmsVehicleWeaponAccessoryGear, cmsCyberware,
                                        cmsCyberwareGear, cmsVehicle, () => objVehicle.Mods.Count, y,
                                        MakeDirtyWithCharacterUpdate, token: token);

                                async void FuncVehicleWeaponsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objVehicle.RefreshChildrenWeapons(
                                        treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                        cmsVehicleWeaponAccessoryGear,
                                        () => objVehicle.Mods.Count + (objVehicle.WeaponMounts.Count > 0 ? 1 : 0),
                                        y, MakeDirtyWithCharacterUpdate, token: token);

                                await objVehicle.Mods.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objVehicle.Mods.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleModsToAdd, token);
                                await objVehicle.WeaponMounts.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objVehicle.WeaponMounts.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleWeaponMountsToAdd, token);
                                await objVehicle.Weapons.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objVehicle.Weapons.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleWeaponsToAdd, token);
                                foreach (VehicleMod objMod in objVehicle.Mods)
                                {
                                    async void FuncVehicleModCyberwareToAdd(
                                        object x, NotifyCollectionChangedEventArgs y) =>
                                        await objMod.RefreshChildrenCyberware(
                                            treVehicles, cmsCyberware, cmsCyberwareGear, null, y,
                                            MakeDirtyWithCharacterUpdate, token: token);

                                    async void FuncVehicleModWeaponsToAdd(
                                        object x, NotifyCollectionChangedEventArgs y) =>
                                        await objMod.RefreshChildrenWeapons(
                                            treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                            cmsVehicleWeaponAccessoryGear, () => objMod.Cyberware.Count, y,
                                            MakeDirtyWithCharacterUpdate, token: token);

                                    await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                        treVehicles, MakeDirtyWithCharacterUpdate, token);
                                    await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                        treVehicles, FuncVehicleModCyberwareToAdd, token);
                                    foreach (Cyberware objCyberware in objMod.Cyberware)
                                        objCyberware.SetupChildrenCyberwareCollectionChanged(true, treVehicles,
                                            cmsCyberware, cmsCyberwareGear, MakeDirtyWithCharacterUpdate);
                                    await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                        treVehicles, MakeDirtyWithCharacterUpdate, token);
                                    await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                        treVehicles, FuncVehicleModWeaponsToAdd, token);
                                    foreach (Weapon objWeapon in objMod.Weapons)
                                        objWeapon.SetupChildrenWeaponsCollectionChanged(true, treVehicles,
                                            cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                            cmsVehicleWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                                }

                                foreach (WeaponMount objMount in objVehicle.WeaponMounts)
                                {
                                    async void FuncWeaponMountVehicleModToAdd(
                                        object x, NotifyCollectionChangedEventArgs y) =>
                                        await objMount.RefreshVehicleMods(treVehicles, cmsVehicle, cmsCyberware,
                                                                          cmsCyberwareGear, cmsVehicleWeapon,
                                                                          cmsVehicleWeaponAccessory,
                                                                          cmsVehicleWeaponAccessoryGear, null, y,
                                                                          MakeDirtyWithCharacterUpdate, token: token);

                                    async void FuncWeaponMountWeaponsToAdd(
                                        object x, NotifyCollectionChangedEventArgs y) =>
                                        await objMount.RefreshChildrenWeapons(
                                            treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                            cmsVehicleWeaponAccessoryGear, () => objMount.Mods.Count, y,
                                            MakeDirtyWithCharacterUpdate, token: token);

                                    await objMount.Mods.AddTaggedCollectionChangedAsync(
                                        treVehicles, MakeDirtyWithCharacterUpdate, token);
                                    await objMount.Mods.AddTaggedCollectionChangedAsync(
                                        treVehicles, FuncWeaponMountVehicleModToAdd, token);
                                    await objMount.Weapons.AddTaggedCollectionChangedAsync(
                                        treVehicles, MakeDirtyWithCharacterUpdate, token);
                                    await objMount.Weapons.AddTaggedCollectionChangedAsync(
                                        treVehicles, FuncWeaponMountWeaponsToAdd, token);
                                    foreach (Weapon objWeapon in objMount.Weapons)
                                        objWeapon.SetupChildrenWeaponsCollectionChanged(true, treVehicles,
                                            cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                            cmsVehicleWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                                    foreach (VehicleMod objMod in objMount.Mods)
                                    {
                                        async void FuncWeaponMountVehicleModCyberwareToAdd(
                                            object x, NotifyCollectionChangedEventArgs y) =>
                                            await objMod.RefreshChildrenCyberware(
                                                treVehicles, cmsCyberware, cmsCyberwareGear, null, y,
                                                MakeDirtyWithCharacterUpdate, token: token);

                                        async void FuncWeaponMountVehicleModWeaponsToAdd(
                                            object x, NotifyCollectionChangedEventArgs y) =>
                                            await objMod.RefreshChildrenWeapons(
                                                treVehicles, cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                                cmsVehicleWeaponAccessoryGear, () => objMod.Cyberware.Count, y,
                                                MakeDirtyWithCharacterUpdate, token: token);

                                        await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                            treVehicles, MakeDirtyWithCharacterUpdate, token);
                                        await objMod.Cyberware.AddTaggedCollectionChangedAsync(
                                            treVehicles, FuncWeaponMountVehicleModCyberwareToAdd, token);
                                        foreach (Cyberware objCyberware in objMod.Cyberware)
                                            objCyberware.SetupChildrenCyberwareCollectionChanged(true, treVehicles,
                                                cmsCyberware, cmsCyberwareGear, MakeDirtyWithCharacterUpdate);
                                        await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                            treVehicles, MakeDirtyWithCharacterUpdate, token);
                                        await objMod.Weapons.AddTaggedCollectionChangedAsync(
                                            treVehicles, FuncWeaponMountVehicleModWeaponsToAdd, token);
                                        foreach (Weapon objWeapon in objMod.Weapons)
                                            objWeapon.SetupChildrenWeaponsCollectionChanged(true, treVehicles,
                                                cmsVehicleWeapon, cmsVehicleWeaponAccessory,
                                                cmsVehicleWeaponAccessoryGear, MakeDirtyWithCharacterUpdate);
                                    }
                                }

                                foreach (Weapon objWeapon in objVehicle.Weapons)
                                    objWeapon.SetupChildrenWeaponsCollectionChanged(
                                        true, treVehicles, cmsVehicleWeapon,
                                        cmsVehicleWeaponAccessory, cmsVehicleWeaponAccessoryGear,
                                        MakeDirtyWithCharacterUpdate);

                                async void FuncVehicleGearToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await objVehicle.RefreshChildrenGears(
                                        treVehicles, cmsVehicleGear,
                                        () => objVehicle.Mods.Count + objVehicle.Weapons.Count
                                                                    + (objVehicle.WeaponMounts.Count > 0 ? 1 : 0),
                                        y, MakeDirtyWithCharacterUpdate, token: token);

                                async void FuncVehicleLocationsToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await RefreshLocationsInVehicle(treVehicles, objVehicle, cmsVehicleLocation,
                                                                    () => objVehicle.Mods.Count
                                                                          + objVehicle.Weapons.Count
                                                                          + (objVehicle.WeaponMounts.Count > 0 ? 1 : 0)
                                                                          + objVehicle.GearChildren.Count(
                                                                              z => z.Location == null), y, token);

                                await objVehicle.GearChildren.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objVehicle.GearChildren.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleGearToAdd, token);
                                foreach (Gear objGear in objVehicle.GearChildren)
                                    objGear.SetupChildrenGearsCollectionChanged(
                                        true, treVehicles, cmsVehicleGear, MakeDirtyWithCharacterUpdate);
                                await objVehicle.Locations.AddTaggedCollectionChangedAsync(
                                    treVehicles, MakeDirtyWithCharacterUpdate, token);
                                await objVehicle.Locations.AddTaggedCollectionChangedAsync(
                                    treVehicles, FuncVehicleLocationsToAdd, token);

                                ++intNewIndex;
                            }

                            await treVehicles.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId),
                                                                token);
                            break;
                        }
                        case NotifyCollectionChangedAction.Move:
                        {
                            await treVehicles.DoThreadSafeAsync(x =>
                            {
                                foreach (Vehicle objVehicle in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    x.FindNodeByTag(objVehicle)?.Remove();
                                }
                            }, token);

                            int intNewIndex = notifyCollectionChangedEventArgs.NewStartingIndex;
                            foreach (Vehicle objVehicle in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objVehicle, intNewIndex);
                                ++intNewIndex;
                            }

                            await treVehicles.DoThreadSafeAsync(x => x.SelectedNode = x.FindNode(strSelectedId),
                                                                token);
                            break;
                        }
                    }
                }

                async ValueTask AddToTree(Vehicle objVehicle, int intIndex = -1, bool blnSingleAdd = true)
                {
                    TreeNode objNode = objVehicle.CreateTreeNode(cmsVehicle, cmsVehicleLocation, cmsVehicleWeapon,
                                                                 cmsVehicleWeaponAccessory,
                                                                 cmsVehicleWeaponAccessoryGear, cmsVehicleGear,
                                                                 cmsVehicleWeaponMount,
                                                                 cmsCyberware, cmsCyberwareGear);
                    if (objNode == null)
                        return;

                    TreeNode nodParent = null;
                    if (objVehicle.Location != null)
                    {
                        nodParent = await treVehicles.DoThreadSafeFuncAsync(
                            x => x.FindNodeByTag(objVehicle.Location, false), token);
                    }

                    if (nodParent == null)
                    {
                        if (nodRoot == null)
                        {
                            nodRoot = new TreeNode
                            {
                                Tag = "Node_SelectedVehicles",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedVehicles", token: token)
                            };
                            await treVehicles.DoThreadSafeAsync(x => x.Nodes.Insert(0, nodRoot), token);
                        }

                        nodParent = nodRoot;
                    }

                    await treVehicles.DoThreadSafeAsync(x =>
                    {
                        if (intIndex >= 0)
                            nodParent.Nodes.Insert(intIndex, objNode);
                        else
                            nodParent.Nodes.Add(objNode);
                        nodParent.Expand();
                        if (blnSingleAdd)
                            x.SelectedNode = objNode;
                    }, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        public async ValueTask RefreshFociFromGear(TreeView treFoci, ContextMenuStrip cmsFocus, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treFoci == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treFoci.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as IHasInternalId)
                    ?.InternalId ?? string.Empty;

                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await treFoci.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await treFoci.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                        int intFociTotal = 0;

                        int intMaxFocusTotal = (await (await CharacterObject.GetAttributeAsync("MAG", token: token))
                            .GetTotalValueAsync(token)) * 5;
                        if (CharacterObjectSettings.MysAdeptSecondMAGAttribute && CharacterObject.IsMysticAdept)
                            intMaxFocusTotal = Math.Min(intMaxFocusTotal, (await (await CharacterObject.GetAttributeAsync("MAGAdept", token: token))
                                                            .GetTotalValueAsync(token)) * 5);

                        foreach (Gear objGear in CharacterObject.Gear)
                        {
                            switch (objGear.Category)
                            {
                                case "Foci":
                                case "Metamagic Foci":
                                {
                                    TreeNode objNode = objGear.CreateTreeNode(cmsFocus);
                                    if (objNode == null)
                                        continue;
                                    objNode.Text = await objNode.Text.CheapReplaceAsync(
                                        await LanguageManager.GetStringAsync("String_Rating", token: token),
                                        () => LanguageManager.GetStringAsync(objGear.RatingLabel, token: token), token: token);
                                    for (int i = CharacterObject.Foci.Count - 1; i >= 0; --i)
                                    {
                                        if (i < CharacterObject.Foci.Count)
                                        {
                                            Focus objFocus = CharacterObject.Foci[i];
                                            if (objFocus.GearObject == objGear)
                                            {
                                                intFociTotal += objFocus.Rating;
                                                // Do not let the number of BP spend on bonded Foci exceed MAG * 5.
                                                if (intFociTotal > intMaxFocusTotal && !CharacterObject.IgnoreRules)
                                                {
                                                    objGear.Bonded = false;
                                                    await CharacterObject.Foci.RemoveAtAsync(i, token: token);
                                                    objNode.Checked = false;
                                                }
                                                else
                                                    objNode.Checked = true;
                                            }
                                        }
                                    }

                                    await AddToTree(objNode, false);
                                }
                                    break;

                                case "Stacked Focus":
                                {
                                    foreach (StackedFocus objStack in CharacterObject.StackedFoci)
                                    {
                                        if (objStack.GearId == objGear.InternalId)
                                        {
                                            await ImprovementManager.RemoveImprovementsAsync(CharacterObject,
                                                Improvement.ImprovementSource.StackedFocus, objStack.InternalId, token);

                                            if (objStack.Bonded)
                                            {
                                                foreach (Gear objFociGear in objStack.Gear)
                                                {
                                                    if (!string.IsNullOrEmpty(objFociGear.Extra))
                                                        ImprovementManager.ForcedValue = objFociGear.Extra;
                                                    await ImprovementManager.CreateImprovementsAsync(CharacterObject,
                                                        Improvement.ImprovementSource.StackedFocus, objStack.InternalId,
                                                        objFociGear.Bonus, objFociGear.Rating,
                                                        await objFociGear.DisplayNameShortAsync(
                                                            GlobalSettings.Language, token), token: token);
                                                    if (objFociGear.WirelessOn)
                                                        await ImprovementManager.CreateImprovementsAsync(
                                                            CharacterObject,
                                                            Improvement.ImprovementSource.StackedFocus,
                                                            objStack.InternalId,
                                                            objFociGear.WirelessBonus, objFociGear.Rating,
                                                            await objFociGear.DisplayNameShortAsync(
                                                                GlobalSettings.Language, token), token: token);
                                                }
                                            }

                                            await AddToTree(objStack.CreateTreeNode(objGear, cmsFocus), false);
                                        }
                                    }
                                }
                                    break;
                            }
                        }

                        await treFoci.DoThreadSafeAsync(x => x.SortCustomAlphabetically(strSelectedId), token);
                    }
                    finally
                    {
                        await treFoci.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            bool blnWarned = false;
                            int intMaxFocusTotal = (await (await CharacterObject.GetAttributeAsync("MAG", token: token))
                                .GetTotalValueAsync(token)) * 5;
                            if (CharacterObjectSettings.MysAdeptSecondMAGAttribute && CharacterObject.IsMysticAdept)
                                intMaxFocusTotal = Math.Min(intMaxFocusTotal, (await (await CharacterObject.GetAttributeAsync("MAGAdept", token: token))
                                                                .GetTotalValueAsync(token)) * 5);

                            HashSet<Gear> setNewGears = new HashSet<Gear>();
                            foreach (Gear objGear in notifyCollectionChangedEventArgs.NewItems)
                                setNewGears.Add(objGear);

                            int intFociTotal = await CharacterObject.Foci.SumAsync(x => !setNewGears.Contains(x.GearObject), x => x.Rating, token);

                            foreach (Gear objGear in notifyCollectionChangedEventArgs.NewItems)
                            {
                                switch (objGear.Category)
                                {
                                    case "Foci":
                                    case "Metamagic Foci":
                                    {
                                        TreeNode objNode = objGear.CreateTreeNode(cmsFocus);
                                        if (objNode == null)
                                            continue;
                                        objNode.Text = await objNode.Text.CheapReplaceAsync(
                                            await LanguageManager.GetStringAsync("String_Rating", token: token),
                                            () => LanguageManager.GetStringAsync("String_Force", token: token), token: token);
                                        for (int i = CharacterObject.Foci.Count - 1; i >= 0; --i)
                                        {
                                            if (i < CharacterObject.Foci.Count)
                                            {
                                                Focus objFocus = CharacterObject.Foci[i];
                                                if (objFocus.GearObject == objGear)
                                                {
                                                    intFociTotal += objFocus.Rating;
                                                    // Do not let the number of BP spend on bonded Foci exceed MAG * 5.
                                                    if (intFociTotal > intMaxFocusTotal && !CharacterObject.IgnoreRules)
                                                    {
                                                        // Mark the Gear a Bonded.
                                                        objGear.Bonded = false;
                                                        await CharacterObject.Foci.RemoveAtAsync(i, token: token);
                                                        objNode.Checked = false;
                                                        if (!blnWarned)
                                                        {
                                                            Program.ShowMessageBox(this,
                                                                await LanguageManager.GetStringAsync(
                                                                    "Message_FocusMaximumForce", token: token),
                                                                await LanguageManager.GetStringAsync(
                                                                    "MessageTitle_FocusMaximum", token: token),
                                                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                                                            blnWarned = true;
                                                            break;
                                                        }
                                                    }
                                                    else
                                                        objNode.Checked = true;
                                                }
                                            }
                                        }

                                        await AddToTree(objNode);
                                    }
                                        break;

                                    case "Stacked Focus":
                                    {
                                        foreach (StackedFocus objStack in CharacterObject.StackedFoci)
                                        {
                                            if (objStack.GearId == objGear.InternalId)
                                            {
                                                await ImprovementManager.RemoveImprovementsAsync(CharacterObject,
                                                    Improvement.ImprovementSource.StackedFocus, objStack.InternalId, token);

                                                if (objStack.Bonded)
                                                {
                                                    foreach (Gear objFociGear in objStack.Gear)
                                                    {
                                                        if (!string.IsNullOrEmpty(objFociGear.Extra))
                                                            ImprovementManager.ForcedValue = objFociGear.Extra;
                                                        await ImprovementManager.CreateImprovementsAsync(
                                                            CharacterObject,
                                                            Improvement.ImprovementSource.StackedFocus,
                                                            objStack.InternalId, objFociGear.Bonus, objFociGear.Rating,
                                                            await objFociGear.DisplayNameShortAsync(
                                                                GlobalSettings.Language, token), token: token);
                                                        if (objFociGear.WirelessOn)
                                                            await ImprovementManager.CreateImprovementsAsync(
                                                                CharacterObject,
                                                                Improvement.ImprovementSource.StackedFocus,
                                                                objStack.InternalId, objFociGear.WirelessBonus,
                                                                objFociGear.Rating,
                                                                await objFociGear.DisplayNameShortAsync(
                                                                    GlobalSettings.Language, token), token: token);
                                                    }
                                                }

                                                await AddToTree(objStack.CreateTreeNode(objGear, cmsFocus));
                                            }
                                        }
                                    }
                                        break;
                                }
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (Gear objGear in notifyCollectionChangedEventArgs.OldItems)
                            {
                                switch (objGear.Category)
                                {
                                    case "Foci":
                                    case "Metamagic Foci":
                                    {
                                        for (int i = CharacterObject.Foci.Count - 1; i >= 0; --i)
                                        {
                                            if (i < CharacterObject.Foci.Count)
                                            {
                                                Focus objFocus = CharacterObject.Foci[i];
                                                if (objFocus.GearObject == objGear)
                                                {
                                                    await CharacterObject.Foci.RemoveAtAsync(i, token: token);
                                                }
                                            }
                                        }

                                        await treFoci.DoThreadSafeAsync(x => x.FindNodeByTag(objGear)?.Remove(),
                                                                        token);
                                    }
                                        break;

                                    case "Stacked Focus":
                                    {
                                        for (int i = CharacterObject.StackedFoci.Count - 1; i >= 0; --i)
                                        {
                                            if (i < CharacterObject.StackedFoci.Count)
                                            {
                                                StackedFocus objStack = CharacterObject.StackedFoci[i];
                                                if (objStack.GearId == objGear.InternalId)
                                                {
                                                    await CharacterObject.StackedFoci.RemoveAtAsync(i, token: token);
                                                    await treFoci.DoThreadSafeAsync(
                                                        x => x.FindNodeByTag(objStack)?.Remove(), token);
                                                    objStack.Dispose();
                                                }
                                            }
                                        }
                                    }
                                        break;
                                }
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Replace:
                        {
                            foreach (Gear objGear in notifyCollectionChangedEventArgs.OldItems)
                            {
                                switch (objGear.Category)
                                {
                                    case "Foci":
                                    case "Metamagic Foci":
                                    {
                                        for (int i = CharacterObject.Foci.Count - 1; i >= 0; --i)
                                        {
                                            if (i < CharacterObject.Foci.Count)
                                            {
                                                Focus objFocus = CharacterObject.Foci[i];
                                                if (objFocus.GearObject == objGear)
                                                {
                                                    await CharacterObject.Foci.RemoveAtAsync(i, token: token);
                                                }
                                            }
                                        }

                                        await treFoci.DoThreadSafeAsync(x => x.FindNodeByTag(objGear)?.Remove(),
                                                                        token);
                                    }
                                        break;

                                    case "Stacked Focus":
                                    {
                                        for (int i = CharacterObject.StackedFoci.Count - 1; i >= 0; --i)
                                        {
                                            if (i < CharacterObject.StackedFoci.Count)
                                            {
                                                StackedFocus objStack = CharacterObject.StackedFoci[i];
                                                if (objStack.GearId == objGear.InternalId)
                                                {
                                                    await CharacterObject.StackedFoci.RemoveAtAsync(i, token: token);
                                                    await treFoci.DoThreadSafeAsync(
                                                        x => x.FindNodeByTag(objStack)?.Remove(), token);
                                                    objStack.Dispose();
                                                }
                                            }
                                        }
                                    }
                                        break;
                                }
                            }

                            bool blnWarned = false;
                            int intMaxFocusTotal = (await (await CharacterObject.GetAttributeAsync("MAG", token: token))
                                .GetTotalValueAsync(token)) * 5;
                            if (CharacterObjectSettings.MysAdeptSecondMAGAttribute && CharacterObject.IsMysticAdept)
                                intMaxFocusTotal = Math.Min(intMaxFocusTotal, (await (await CharacterObject.GetAttributeAsync("MAGAdept", token: token))
                                                                .GetTotalValueAsync(token)) * 5);

                            HashSet<Gear> setNewGears = new HashSet<Gear>();
                            foreach (Gear objGear in notifyCollectionChangedEventArgs.NewItems)
                                setNewGears.Add(objGear);

                            int intFociTotal = await CharacterObject.Foci.SumAsync(x => !setNewGears.Contains(x.GearObject), x => x.Rating, token);

                            foreach (Gear objGear in notifyCollectionChangedEventArgs.NewItems)
                            {
                                switch (objGear.Category)
                                {
                                    case "Foci":
                                    case "Metamagic Foci":
                                    {
                                        TreeNode objNode = objGear.CreateTreeNode(cmsFocus);
                                        if (objNode == null)
                                            continue;
                                        objNode.Text = await objNode.Text.CheapReplaceAsync(
                                            await LanguageManager.GetStringAsync("String_Rating", token: token),
                                            () => LanguageManager.GetString("String_Force", token: token), token: token);
                                        for (int i = CharacterObject.Foci.Count - 1; i >= 0; --i)
                                        {
                                            if (i < CharacterObject.Foci.Count)
                                            {
                                                Focus objFocus = CharacterObject.Foci[i];
                                                if (objFocus.GearObject == objGear)
                                                {
                                                    intFociTotal += objFocus.Rating;
                                                    // Do not let the number of BP spend on bonded Foci exceed MAG * 5.
                                                    if (intFociTotal > intMaxFocusTotal && !CharacterObject.IgnoreRules)
                                                    {
                                                        // Mark the Gear a Bonded.
                                                        objGear.Bonded = false;
                                                        await CharacterObject.Foci.RemoveAtAsync(i, token: token);
                                                        objNode.Checked = false;
                                                        if (!blnWarned)
                                                        {
                                                            Program.ShowMessageBox(this,
                                                                await LanguageManager.GetStringAsync(
                                                                    "Message_FocusMaximumForce", token: token),
                                                                await LanguageManager.GetStringAsync(
                                                                    "MessageTitle_FocusMaximum", token: token),
                                                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                                                            blnWarned = true;
                                                            break;
                                                        }
                                                    }
                                                    else
                                                        objNode.Checked = true;
                                                }
                                            }
                                        }

                                        await AddToTree(objNode);
                                    }
                                        break;

                                    case "Stacked Focus":
                                    {
                                        foreach (StackedFocus objStack in CharacterObject.StackedFoci)
                                        {
                                            if (objStack.GearId == objGear.InternalId)
                                            {
                                                await ImprovementManager.RemoveImprovementsAsync(CharacterObject,
                                                    Improvement.ImprovementSource.StackedFocus, objStack.InternalId, token);

                                                if (objStack.Bonded)
                                                {
                                                    foreach (Gear objFociGear in objStack.Gear)
                                                    {
                                                        if (!string.IsNullOrEmpty(objFociGear.Extra))
                                                            ImprovementManager.ForcedValue = objFociGear.Extra;
                                                        await ImprovementManager.CreateImprovementsAsync(
                                                            CharacterObject,
                                                            Improvement.ImprovementSource.StackedFocus,
                                                            objStack.InternalId, objFociGear.Bonus, objFociGear.Rating,
                                                            await objFociGear.DisplayNameShortAsync(
                                                                GlobalSettings.Language, token), token: token);
                                                        if (objFociGear.WirelessOn)
                                                            await ImprovementManager.CreateImprovementsAsync(
                                                                CharacterObject,
                                                                Improvement.ImprovementSource.StackedFocus,
                                                                objStack.InternalId, objFociGear.WirelessBonus,
                                                                objFociGear.Rating,
                                                                await objFociGear.DisplayNameShortAsync(
                                                                    GlobalSettings.Language, token), token: token);
                                                    }
                                                }

                                                await AddToTree(objStack.CreateTreeNode(objGear, cmsFocus));
                                            }
                                        }
                                    }
                                        break;
                                }
                            }
                        }
                            break;
                    }
                }

                Task AddToTree(TreeNode objNode, bool blnSingleAdd = true)
                {
                    return treFoci.DoThreadSafeAsync(x =>
                    {
                        TreeNodeCollection lstParentNodeChildren = x.Nodes;
                        if (blnSingleAdd)
                        {
                            int intNodesCount = lstParentNodeChildren.Count;
                            int intTargetIndex = 0;
                            for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                            {
                                if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                                {
                                    break;
                                }
                            }

                            lstParentNodeChildren.Insert(intTargetIndex, objNode);
                            x.SelectedNode = objNode;
                        }
                        else
                            lstParentNodeChildren.Add(objNode);
                    }, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshMartialArts(TreeView treMartialArts, ContextMenuStrip cmsMartialArts, ContextMenuStrip cmsTechnique, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treMartialArts == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treMartialArts.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                        IHasInternalId)?.InternalId ?? string.Empty;

                TreeNode objMartialArtsParentNode = null;
                TreeNode objQualityNode = null;

                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await treMartialArts.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await treMartialArts.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                        foreach (MartialArt objMartialArt in CharacterObject.MartialArts)
                        {
                            await AddToTree(objMartialArt, false);
                            await objMartialArt.Techniques.AddTaggedCollectionChangedAsync(
                                treMartialArts, MakeDirtyWithCharacterUpdate, token);
                            await objMartialArt.Techniques.AddTaggedCollectionChangedAsync(
                                treMartialArts, FuncDelegateToAdd, token);

                            async void FuncDelegateToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                await RefreshMartialArtTechniques(treMartialArts, objMartialArt, cmsTechnique, y, token);
                        }

                        await treMartialArts.DoThreadSafeAsync(x => x.SortCustomAlphabetically(strSelectedId),
                                                               token);
                    }
                    finally
                    {
                        await treMartialArts.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    objMartialArtsParentNode
                        = await treMartialArts.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedMartialArts", false),
                                                                     token);
                    objQualityNode
                        = await treMartialArts.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedQualities", false),
                                                                     token);
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (MartialArt objMartialArt in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objMartialArt);
                                await objMartialArt.Techniques.AddTaggedCollectionChangedAsync(
                                    treMartialArts, MakeDirtyWithCharacterUpdate, token);
                                await objMartialArt.Techniques.AddTaggedCollectionChangedAsync(
                                    treMartialArts, FuncDelegateToAdd, token);

                                async void FuncDelegateToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await RefreshMartialArtTechniques(treMartialArts, objMartialArt, cmsTechnique, y, token);
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (MartialArt objMartialArt in notifyCollectionChangedEventArgs.OldItems)
                            {
                                await objMartialArt.Techniques.RemoveTaggedCollectionChangedAsync(treMartialArts, token);
                                await treMartialArts.DoThreadSafeAsync(x =>
                                {
                                    TreeNode objNode = x.FindNodeByTag(objMartialArt);
                                    if (objNode != null)
                                    {
                                        TreeNode objParent = objNode.Parent;
                                        objNode.Remove();
                                        if (objParent.Nodes.Count == 0)
                                            objParent.Remove();
                                    }
                                }, token);
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Replace:
                        {
                            List<TreeNode> lstOldParents =
                                new List<TreeNode>(notifyCollectionChangedEventArgs.OldItems.Count);
                            foreach (MartialArt objMartialArt in notifyCollectionChangedEventArgs.OldItems)
                            {
                                await objMartialArt.Techniques.RemoveTaggedCollectionChangedAsync(treMartialArts, token);
                                await treMartialArts.DoThreadSafeAsync(x =>
                                {
                                    TreeNode objNode = x.FindNodeByTag(objMartialArt);
                                    if (objNode != null)
                                    {
                                        lstOldParents.Add(objNode.Parent);
                                        objNode.Remove();
                                    }
                                }, token);
                            }

                            foreach (MartialArt objMartialArt in notifyCollectionChangedEventArgs.NewItems)
                            {
                                await AddToTree(objMartialArt);
                                await objMartialArt.Techniques.AddTaggedCollectionChangedAsync(
                                    treMartialArts, MakeDirtyWithCharacterUpdate, token);
                                await objMartialArt.Techniques.AddTaggedCollectionChangedAsync(
                                    treMartialArts, FuncDelegateToAdd, token);

                                async void FuncDelegateToAdd(object x, NotifyCollectionChangedEventArgs y) =>
                                    await RefreshMartialArtTechniques(treMartialArts, objMartialArt, cmsTechnique, y, token);
                            }

                            await treMartialArts.DoThreadSafeAsync(() =>
                            {
                                foreach (TreeNode objOldParent in lstOldParents)
                                {
                                    if (objOldParent.Nodes.Count == 0)
                                        objOldParent.Remove();
                                }
                            }, token);
                        }
                            break;
                    }
                }

                async ValueTask AddToTree(MartialArt objMartialArt, bool blnSingleAdd = true)
                {
                    TreeNode objNode = objMartialArt.CreateTreeNode(cmsMartialArts, cmsTechnique);
                    if (objNode == null)
                        return;

                    TreeNode objParentNode;
                    if (objMartialArt.IsQuality)
                    {
                        if (objQualityNode == null)
                        {
                            objQualityNode = new TreeNode
                            {
                                Tag = "Node_SelectedQualities",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedQualities", token: token)
                            };
                            await treMartialArts.DoThreadSafeAsync(x =>
                            {
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Add(objQualityNode);
                                objQualityNode.Expand();
                            }, token);
                        }

                        objParentNode = objQualityNode;
                    }
                    else
                    {
                        if (objMartialArtsParentNode == null)
                        {
                            objMartialArtsParentNode = new TreeNode
                            {
                                Tag = "Node_SelectedMartialArts",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedMartialArts", token: token)
                            };
                            await treMartialArts.DoThreadSafeAsync(x =>
                            {
                                // ReSharper disable once AssignNullToNotNullAttribute
                                x.Nodes.Insert(0, objMartialArtsParentNode);
                                objMartialArtsParentNode.Expand();
                            }, token);
                        }

                        objParentNode = objMartialArtsParentNode;
                    }

                    await treMartialArts.DoThreadSafeAsync(x =>
                    {
                        if (objParentNode == null)
                            return;
                        if (blnSingleAdd)
                        {
                            TreeNodeCollection lstParentNodeChildren = objParentNode.Nodes;
                            int intNodesCount = lstParentNodeChildren.Count;
                            int intTargetIndex = 0;
                            for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                            {
                                if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                                {
                                    break;
                                }
                            }

                            lstParentNodeChildren.Insert(intTargetIndex, objNode);
                            x.SelectedNode = objNode;
                        }
                        else
                            objParentNode.Nodes.Add(objNode);

                        objParentNode.Expand();
                    }, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshMartialArtTechniques(TreeView treMartialArts, MartialArt objMartialArt, ContextMenuStrip cmsTechnique, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs, CancellationToken token = default)
        {
            if (treMartialArts == null || objMartialArt == null || notifyCollectionChangedEventArgs == null)
                return;
            TreeNode nodMartialArt = await treMartialArts.DoThreadSafeFuncAsync(x => x.FindNodeByTag(objMartialArt), token);
            if (nodMartialArt == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                switch (notifyCollectionChangedEventArgs.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                    {
                        await treMartialArts.DoThreadSafeAsync(() =>
                        {
                            foreach (MartialArtTechnique objTechnique in notifyCollectionChangedEventArgs.NewItems)
                            {
                                AddToTree(objTechnique);
                            }
                        }, token);
                    }
                        break;

                    case NotifyCollectionChangedAction.Remove:
                    {
                        await treMartialArts.DoThreadSafeAsync(() =>
                        {
                            foreach (MartialArtTechnique objTechnique in notifyCollectionChangedEventArgs.OldItems)
                            {
                                nodMartialArt.FindNodeByTag(objTechnique)?.Remove();
                            }
                        }, token);
                    }
                        break;

                    case NotifyCollectionChangedAction.Replace:
                    {
                        await treMartialArts.DoThreadSafeAsync(() =>
                        {
                            foreach (MartialArtTechnique objTechnique in notifyCollectionChangedEventArgs.OldItems)
                            {
                                nodMartialArt.FindNodeByTag(objTechnique)?.Remove();
                            }

                            foreach (MartialArtTechnique objTechnique in notifyCollectionChangedEventArgs.NewItems)
                            {
                                AddToTree(objTechnique);
                            }
                        }, token);
                    }
                        break;

                    case NotifyCollectionChangedAction.Reset:
                    {
                        await treMartialArts.DoThreadSafeAsync(x =>
                        {
                            string strSelectedId = (x.SelectedNode?.Tag as IHasInternalId)?.InternalId ?? string.Empty;

                            nodMartialArt.Nodes.Clear();

                            foreach (MartialArtTechnique objTechnique in objMartialArt.Techniques)
                            {
                                AddToTree(objTechnique, false);
                            }

                            x.SortCustomAlphabetically(strSelectedId);
                        }, token);
                    }
                        break;
                }

                void AddToTree(MartialArtTechnique objTechnique, bool blnSingleAdd = true)
                {
                    TreeNode objNode = objTechnique.CreateTreeNode(cmsTechnique);
                    if (objNode == null)
                        return;

                    if (blnSingleAdd)
                    {
                        TreeNodeCollection lstParentNodeChildren = nodMartialArt.Nodes;
                        int intNodesCount = lstParentNodeChildren.Count;
                        int intTargetIndex = 0;
                        for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                        {
                            if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                            {
                                break;
                            }
                        }

                        lstParentNodeChildren.Insert(intTargetIndex, objNode);
                        treMartialArts.SelectedNode = objNode;
                    }
                    else
                        nodMartialArt.Nodes.Add(objNode);

                    nodMartialArt.Expand();
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        /// <summary>
        /// Refresh the list of Improvements.
        /// </summary>
        protected async ValueTask RefreshCustomImprovements(TreeView treImprovements, TreeView treLimit, ContextMenuStrip cmsImprovementLocation, ContextMenuStrip cmsImprovement, ContextMenuStrip cmsLimitModifier, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (treImprovements == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId =
                    (await treImprovements.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag as IHasInternalId,
                                                                 token))?.InternalId ?? string.Empty;

                TreeNode objRoot;

                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await treImprovements.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await treImprovements.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                        objRoot = new TreeNode
                        {
                            Tag = "Node_SelectedImprovements",
                            Text = await LanguageManager.GetStringAsync("Node_SelectedImprovements", token: token)
                        };
                        await treImprovements.DoThreadSafeAsync(x => x.Nodes.Add(objRoot), token);

                        // Add the Locations.
                        foreach (string strGroup in CharacterObject.ImprovementGroups)
                        {
                            TreeNode objGroup = new TreeNode
                            {
                                Tag = strGroup,
                                Text = strGroup,
                                ContextMenuStrip = cmsImprovementLocation
                            };
                            await treImprovements.DoThreadSafeAsync(x => x.Nodes.Add(objGroup), token);
                        }

                        foreach (Improvement objImprovement in CharacterObject.Improvements)
                        {
                            if (objImprovement.ImproveSource == Improvement.ImprovementSource.Custom ||
                                objImprovement.ImproveSource == Improvement.ImprovementSource.Drug)
                            {
                                await AddToTree(objImprovement, false);
                            }
                        }

                        // Sort the list of Custom Improvements in alphabetical order based on their Custom Name within each Group.
                        await treImprovements.DoThreadSafeAsync(x => x.SortCustomAlphabetically(strSelectedId),
                                                                token);
                    }
                    finally
                    {
                        await treImprovements.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    objRoot = await treImprovements.DoThreadSafeFuncAsync(
                        x => x.FindNode("Node_SelectedImprovements", false), token);
                    TreeNode[] aobjLimitNodes = new TreeNode[4];
                    if (treLimit != null)
                        await treLimit.DoThreadSafeAsync(x =>
                        {
                            aobjLimitNodes[0] = x.FindNode("Node_Physical", false);
                            aobjLimitNodes[1] = x.FindNode("Node_Mental", false);
                            aobjLimitNodes[2] = x.FindNode("Node_Social", false);
                            aobjLimitNodes[3] = x.FindNode("Node_Astral", false);
                        }, token);

                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (Improvement objImprovement in notifyCollectionChangedEventArgs.NewItems)
                            {
                                if (objImprovement.ImproveSource == Improvement.ImprovementSource.Custom ||
                                    objImprovement.ImproveSource == Improvement.ImprovementSource.Drug)
                                {
                                    await AddToTree(objImprovement);
                                    await AddToLimitTree(objImprovement);
                                }
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Remove:
                        {
                            await treImprovements.DoThreadSafeAsync(x =>
                            {
                                foreach (Improvement objImprovement in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    if (objImprovement.ImproveSource == Improvement.ImprovementSource.Custom ||
                                        objImprovement.ImproveSource == Improvement.ImprovementSource.Drug)
                                    {
                                        TreeNode objNode = x.FindNodeByTag(objImprovement);
                                        if (objNode != null)
                                        {
                                            TreeNode objParent = objNode.Parent;
                                            objNode.Remove();
                                            if (objParent.Tag.ToString() == "Node_SelectedImprovements" &&
                                                objParent.Nodes.Count == 0)
                                                objParent.Remove();
                                        }

                                        treLimit?.DoThreadSafe(y =>
                                        {
                                            objNode = y.FindNodeByTag(objImprovement);
                                            if (objNode != null)
                                            {
                                                TreeNode objParent = objNode.Parent;
                                                objNode.Remove();
                                                if (objParent.Level == 0 && objParent.Nodes.Count == 0)
                                                    objParent.Remove();
                                            }
                                        });
                                    }
                                }
                            }, token);

                            break;
                        }
                        case NotifyCollectionChangedAction.Replace:
                        {
                            List<TreeNode> lstOldParents =
                                new List<TreeNode>(notifyCollectionChangedEventArgs.OldItems.Count);
                            await treImprovements.DoThreadSafeAsync(x =>
                            {
                                foreach (Improvement objImprovement in notifyCollectionChangedEventArgs.OldItems)
                                {
                                    if (objImprovement.ImproveSource == Improvement.ImprovementSource.Custom ||
                                        objImprovement.ImproveSource == Improvement.ImprovementSource.Drug)
                                    {
                                        TreeNode objNode = x.FindNodeByTag(objImprovement);
                                        if (objNode != null)
                                        {
                                            lstOldParents.Add(objNode.Parent);
                                            objNode.Remove();
                                        }

                                        treLimit?.DoThreadSafe(y =>
                                        {
                                            objNode = y.FindNodeByTag(objImprovement);
                                            if (objNode != null)
                                            {
                                                lstOldParents.Add(objNode.Parent);
                                                objNode.Remove();
                                            }
                                        });
                                    }
                                }
                            }, token);

                            foreach (Improvement objImprovement in notifyCollectionChangedEventArgs.NewItems)
                            {
                                if (objImprovement.ImproveSource == Improvement.ImprovementSource.Custom ||
                                    objImprovement.ImproveSource == Improvement.ImprovementSource.Drug)
                                {
                                    await AddToTree(objImprovement);
                                    await AddToLimitTree(objImprovement);
                                }
                            }

                            await treImprovements.DoThreadSafeAsync(() =>
                            {
                                foreach (TreeNode objOldParent in lstOldParents)
                                {
                                    if (objOldParent.Level == 0 && objOldParent.Nodes.Count == 0)
                                        objOldParent.Remove();
                                }
                            }, token);

                            break;
                        }
                    }

                    async ValueTask AddToLimitTree(Improvement objImprovement)
                    {
                        if (treLimit == null)
                            return;
                        int intTargetLimit = -1;
                        switch (objImprovement.ImproveType)
                        {
                            case Improvement.ImprovementType.LimitModifier:
                                intTargetLimit = (int) Enum.Parse(typeof(LimitType), objImprovement.ImprovedName);
                                break;

                            case Improvement.ImprovementType.PhysicalLimit:
                                intTargetLimit = (int) LimitType.Physical;
                                break;

                            case Improvement.ImprovementType.MentalLimit:
                                intTargetLimit = (int) LimitType.Mental;
                                break;

                            case Improvement.ImprovementType.SocialLimit:
                                intTargetLimit = (int) LimitType.Social;
                                break;
                        }

                        if (intTargetLimit != -1)
                        {
                            TreeNode objParentNode = aobjLimitNodes[intTargetLimit];
                            if (objParentNode == null)
                            {
                                switch (intTargetLimit)
                                {
                                    case 0:
                                        objParentNode = new TreeNode
                                        {
                                            Tag = "Node_Physical",
                                            Text = await LanguageManager.GetStringAsync("Node_Physical", token: token)
                                        };
                                        await treLimit.DoThreadSafeAsync(
                                            x => x.Nodes.Insert(0, objParentNode), token);
                                        break;

                                    case 1:
                                        objParentNode = new TreeNode
                                        {
                                            Tag = "Node_Mental",
                                            Text = await LanguageManager.GetStringAsync("Node_Mental", token: token)
                                        };
                                        await treLimit.DoThreadSafeAsync(
                                            x => x.Nodes.Insert(aobjLimitNodes[0] == null ? 0 : 1, objParentNode),
                                            token);
                                        break;

                                    case 2:
                                        objParentNode = new TreeNode
                                        {
                                            Tag = "Node_Social",
                                            Text = await LanguageManager.GetStringAsync("Node_Social", token: token)
                                        };
                                        await treLimit.DoThreadSafeAsync(x => x.Nodes.Insert(
                                                                             (aobjLimitNodes[0] == null ? 0 : 1)
                                                                             + (aobjLimitNodes[1] == null ? 0 : 1),
                                                                             objParentNode), token);
                                        break;

                                    case 3:
                                        objParentNode = new TreeNode
                                        {
                                            Tag = "Node_Astral",
                                            Text = await LanguageManager.GetStringAsync("Node_Astral", token: token)
                                        };
                                        await treLimit.DoThreadSafeAsync(x => x.Nodes.Add(objParentNode), token);
                                        break;
                                }

                                if (objParentNode != null)
                                    await treLimit.DoThreadSafeAsync(() => objParentNode.Expand(), token);
                            }

                            string strName = objImprovement.UniqueName
                                             + await LanguageManager.GetStringAsync("String_Colon", token: token) +
                                             await LanguageManager.GetStringAsync("String_Space", token: token);
                            if (objImprovement.Value > 0)
                                strName += '+';
                            strName += objImprovement.Value.ToString(GlobalSettings.CultureInfo);
                            if (!string.IsNullOrEmpty(objImprovement.Condition))
                                strName += ',' + await LanguageManager.GetStringAsync("String_Space", token: token)
                                               + objImprovement.Condition;
                            if (objParentNode?.Nodes.ContainsKey(strName) == false)
                            {
                                TreeNode objNode = new TreeNode
                                {
                                    Name = strName,
                                    Text = strName,
                                    Tag = objImprovement.SourceName,
                                    ContextMenuStrip = cmsLimitModifier,
                                    ForeColor = objImprovement.PreferredColor,
                                    ToolTipText = objImprovement.Notes.WordWrap()
                                };
                                if (string.IsNullOrEmpty(objImprovement.ImprovedName))
                                {
                                    switch (objImprovement.ImproveType)
                                    {
                                        case Improvement.ImprovementType.SocialLimit:
                                            objImprovement.ImprovedName = "Social";
                                            break;

                                        case Improvement.ImprovementType.MentalLimit:
                                            objImprovement.ImprovedName = "Mental";
                                            break;

                                        default:
                                            objImprovement.ImprovedName = "Physical";
                                            break;
                                    }
                                }

                                await treLimit.DoThreadSafeAsync(x =>
                                {
                                    TreeNodeCollection lstParentNodeChildren = objParentNode.Nodes;
                                    int intNodesCount = lstParentNodeChildren.Count;
                                    int intTargetIndex = 0;
                                    for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                                    {
                                        if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode)
                                            >=
                                            0)
                                        {
                                            break;
                                        }
                                    }

                                    lstParentNodeChildren.Insert(intTargetIndex, objNode);
                                    x.SelectedNode = objNode;
                                }, token);
                            }
                        }
                    }
                }

                async ValueTask AddToTree(Improvement objImprovement, bool blnSingleAdd = true)
                {
                    TreeNode objNode = objImprovement.CreateTreeNode(cmsImprovement);

                    TreeNode objParentNode = objRoot;
                    if (!string.IsNullOrEmpty(objImprovement.CustomGroup))
                    {
                        await treImprovements.DoThreadSafeAsync(x =>
                        {
                            foreach (TreeNode objFind in x.Nodes)
                            {
                                if (objFind.Text == objImprovement.CustomGroup)
                                {
                                    objParentNode = objFind;
                                    break;
                                }
                            }
                        }, token);
                    }
                    else
                    {
                        if (objParentNode == null)
                        {
                            objParentNode = new TreeNode
                            {
                                Tag = "Node_SelectedImprovements",
                                Text = await LanguageManager.GetStringAsync("Node_SelectedImprovements", token: token)
                            };
                            await treImprovements.DoThreadSafeAsync(x => x.Nodes.Add(objParentNode), token);
                        }
                    }

                    await treImprovements.DoThreadSafeAsync(x =>
                    {
                        if (blnSingleAdd)
                        {
                            TreeNodeCollection lstParentNodeChildren = objParentNode.Nodes;
                            int intNodesCount = lstParentNodeChildren.Count;
                            int intTargetIndex = 0;
                            for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                            {
                                if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                                {
                                    break;
                                }
                            }

                            lstParentNodeChildren.Insert(intTargetIndex, objNode);
                            x.SelectedNode = objNode;
                        }
                        else
                            objParentNode.Nodes.Add(objNode);

                        objParentNode.Expand();
                    }, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        protected async ValueTask RefreshLifestyles(TreeView treLifestyles, ContextMenuStrip cmsBasicLifestyle,
                                         ContextMenuStrip cmsAdvancedLifestyle, NotifyCollectionChangedEventArgs e = null, CancellationToken token = default)
        {
            if (treLifestyles == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                string strSelectedId
                    = (await treLifestyles.DoThreadSafeFuncAsync(x => x.SelectedNode?.Tag, token) as
                        IHasInternalId)?.InternalId ?? string.Empty;
                TreeNode objParentNode = null;

                if (e == null || e.Action == NotifyCollectionChangedAction.Reset)
                {
                    await treLifestyles.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await treLifestyles.DoThreadSafeAsync(x => x.Nodes.Clear(), token);

                        if (CharacterObject.Lifestyles.Count > 0)
                        {
                            foreach (Lifestyle objLifestyle in CharacterObject.Lifestyles)
                            {
                                await AddToTree(objLifestyle, false);
                            }

                            await treLifestyles.DoThreadSafeAsync(x => x.SortCustomAlphabetically(strSelectedId),
                                                                  token);
                        }
                    }
                    finally
                    {
                        await treLifestyles.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    objParentNode
                        = await treLifestyles.DoThreadSafeFuncAsync(x => x.FindNode("Node_SelectedLifestyles", false),
                                                                    token);
                    switch (e.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (Lifestyle objLifestyle in e.NewItems)
                            {
                                await AddToTree(objLifestyle);
                            }

                            break;
                        }
                        case NotifyCollectionChangedAction.Remove:
                        {
                            await treLifestyles.DoThreadSafeAsync(x =>
                            {
                                foreach (Lifestyle objLifestyle in e.OldItems)
                                {
                                    TreeNode objNode = x.FindNodeByTag(objLifestyle);
                                    if (objNode != null)
                                    {
                                        TreeNode objParent = objNode.Parent;
                                        objNode.Remove();
                                        if (objParent.Level == 0 && objParent.Nodes.Count == 0)
                                            objParent.Remove();
                                    }
                                }
                            }, token);

                            break;
                        }
                        case NotifyCollectionChangedAction.Replace:
                        {
                            HashSet<TreeNode> setOldParentNodes = new HashSet<TreeNode>();
                            await treLifestyles.DoThreadSafeAsync(x =>
                            {
                                foreach (Lifestyle objLifestyle in e.OldItems)
                                {
                                    TreeNode objNode = x.FindNodeByTag(objLifestyle);
                                    if (objNode != null)
                                    {
                                        setOldParentNodes.Add(objNode.Parent);
                                        objNode.Remove();
                                    }
                                }
                            }, token);

                            foreach (Lifestyle objLifestyle in e.NewItems)
                            {
                                await AddToTree(objLifestyle);
                            }

                            await treLifestyles.DoThreadSafeAsync(() =>
                            {
                                foreach (TreeNode nodOldParent in setOldParentNodes)
                                {
                                    if (nodOldParent.Level == 0 && nodOldParent.Nodes.Count == 0)
                                        nodOldParent.Remove();
                                }
                            }, token);

                            break;
                        }
                    }
                }

                async ValueTask AddToTree(Lifestyle objLifestyle, bool blnSingleAdd = true)
                {
                    TreeNode objNode = objLifestyle.CreateTreeNode(cmsBasicLifestyle, cmsAdvancedLifestyle);
                    if (objNode == null)
                        return;

                    if (objParentNode == null)
                    {
                        objParentNode = new TreeNode
                        {
                            Tag = "Node_SelectedLifestyles",
                            Text = await LanguageManager.GetStringAsync("Node_SelectedLifestyles", token: token)
                        };
                        await treLifestyles.DoThreadSafeAsync(x =>
                        {
                            // ReSharper disable once AssignNullToNotNullAttribute
                            x.Nodes.Add(objParentNode);
                            objParentNode.Expand();
                        }, token);
                    }

                    await treLifestyles.DoThreadSafeAsync(x =>
                    {
                        if (objParentNode == null)
                            return;
                        if (blnSingleAdd)
                        {
                            TreeNodeCollection lstParentNodeChildren = objParentNode.Nodes;
                            int intNodesCount = lstParentNodeChildren.Count;
                            int intTargetIndex = 0;
                            for (; intTargetIndex < intNodesCount; ++intTargetIndex)
                            {
                                if (CompareTreeNodes.CompareText(lstParentNodeChildren[intTargetIndex], objNode) >= 0)
                                {
                                    break;
                                }
                            }

                            lstParentNodeChildren.Insert(intTargetIndex, objNode);
                            x.SelectedNode = objNode;
                        }
                        else
                            objParentNode.Nodes.Add(objNode);
                    }, token);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        /// <summary>
        /// Refresh the Calendar List.
        /// </summary>
        public async ValueTask RefreshCalendar(ListView lstCalendar, ListChangedEventArgs listChangedEventArgs = null, CancellationToken token = default)
        {
            if (lstCalendar == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (listChangedEventArgs == null || listChangedEventArgs.ListChangedType == ListChangedType.Reset)
                {
                    await lstCalendar.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        await lstCalendar.DoThreadSafeAsync(x => x.Items.Clear(), token);
                        foreach (CalendarWeek objWeek in CharacterObject.Calendar)
                        {
                            ListViewItem.ListViewSubItem objNoteItem = new ListViewItem.ListViewSubItem
                            {
                                Text = objWeek.Notes,
                                ForeColor = objWeek.PreferredColor
                            };
                            ListViewItem.ListViewSubItem objInternalIdItem = new ListViewItem.ListViewSubItem
                            {
                                Text = objWeek.InternalId,
                                ForeColor = objWeek.PreferredColor
                            };

                            ListViewItem objItem = new ListViewItem
                            {
                                Text = objWeek.CurrentDisplayName,
                                ForeColor = objWeek.PreferredColor
                            };
                            objItem.SubItems.Add(objNoteItem);
                            objItem.SubItems.Add(objInternalIdItem);

                            await lstCalendar.DoThreadSafeAsync(x => x.Items.Add(objItem), token);
                        }
                    }
                    finally
                    {
                        await lstCalendar.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    switch (listChangedEventArgs.ListChangedType)
                    {
                        case ListChangedType.ItemAdded:
                        {
                            int intInsertIndex = listChangedEventArgs.NewIndex;
                            CalendarWeek objWeek = CharacterObject.Calendar[intInsertIndex];

                            ListViewItem.ListViewSubItem objNoteItem = new ListViewItem.ListViewSubItem
                            {
                                Text = objWeek.Notes,
                                ForeColor = objWeek.PreferredColor
                            };
                            ListViewItem.ListViewSubItem objInternalIdItem = new ListViewItem.ListViewSubItem
                            {
                                Text = objWeek.InternalId,
                                ForeColor = objWeek.PreferredColor
                            };

                            ListViewItem objItem = new ListViewItem
                            {
                                Text = objWeek.CurrentDisplayName,
                                ForeColor = objWeek.PreferredColor
                            };
                            objItem.SubItems.Add(objNoteItem);
                            objItem.SubItems.Add(objInternalIdItem);

                            await lstCalendar.DoThreadSafeAsync(x => x.Items.Insert(intInsertIndex, objItem),
                                                                token);
                        }
                            break;

                        case ListChangedType.ItemDeleted:
                        {
                            await lstCalendar.DoThreadSafeAsync(x => x.Items.RemoveAt(listChangedEventArgs.NewIndex),
                                                                token);
                        }
                            break;

                        case ListChangedType.ItemChanged:
                        {
                            await lstCalendar.DoThreadSafeAsync(x => x.Items.RemoveAt(listChangedEventArgs.NewIndex),
                                                                token);
                            int intInsertIndex = listChangedEventArgs.NewIndex;
                            CalendarWeek objWeek = CharacterObject.Calendar[intInsertIndex];

                            ListViewItem.ListViewSubItem objNoteItem = new ListViewItem.ListViewSubItem
                            {
                                Text = objWeek.Notes,
                                ForeColor = objWeek.PreferredColor
                            };
                            ListViewItem.ListViewSubItem objInternalIdItem = new ListViewItem.ListViewSubItem
                            {
                                Text = objWeek.InternalId,
                                ForeColor = objWeek.PreferredColor
                            };

                            ListViewItem objItem = new ListViewItem
                            {
                                Text = objWeek.CurrentDisplayName,
                                ForeColor = objWeek.PreferredColor
                            };
                            objItem.SubItems.Add(objNoteItem);
                            objItem.SubItems.Add(objInternalIdItem);

                            await lstCalendar.DoThreadSafeAsync(x => x.Items.Insert(intInsertIndex, objItem),
                                                                token);
                        }
                            break;

                        case ListChangedType.ItemMoved:
                        {
                            await lstCalendar.DoThreadSafeAsync(x => x.Items.RemoveAt(listChangedEventArgs.OldIndex),
                                                                token);
                            int intInsertIndex = listChangedEventArgs.NewIndex;
                            CalendarWeek objWeek = CharacterObject.Calendar[intInsertIndex];

                            ListViewItem.ListViewSubItem objNoteItem = new ListViewItem.ListViewSubItem
                            {
                                Text = objWeek.Notes,
                                ForeColor = objWeek.PreferredColor
                            };
                            ListViewItem.ListViewSubItem objInternalIdItem = new ListViewItem.ListViewSubItem
                            {
                                Text = objWeek.InternalId,
                                ForeColor = objWeek.PreferredColor
                            };

                            ListViewItem objItem = new ListViewItem
                            {
                                Text = objWeek.CurrentDisplayName,
                                ForeColor = objWeek.PreferredColor
                            };
                            objItem.SubItems.Add(objNoteItem);
                            objItem.SubItems.Add(objInternalIdItem);

                            await lstCalendar.DoThreadSafeAsync(x => x.Items.Insert(intInsertIndex, objItem),
                                                                token);
                        }
                            break;
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        public async Task RefreshContacts(FlowLayoutPanel panContacts, FlowLayoutPanel panEnemies, FlowLayoutPanel panPets, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (panContacts == null && panEnemies == null && panPets == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    if (panContacts != null)
                        await panContacts.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    if (panEnemies != null)
                        await panEnemies.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    if (panPets != null)
                        await panPets.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        if (panContacts != null)
                            await panContacts.DoThreadSafeAsync(x => x.Controls.Clear(), token);
                        if (panEnemies != null)
                            await panEnemies.DoThreadSafeAsync(x => x.Controls.Clear(), token);
                        if (panPets != null)
                            await panPets.DoThreadSafeAsync(x => x.Controls.Clear(), token);
                        foreach (Contact objContact in CharacterObject.Contacts)
                        {
                            switch (objContact.EntityType)
                            {
                                case ContactType.Contact:
                                {
                                    if (panContacts == null)
                                        break;
                                    await this.DoThreadSafeAsync(() =>
                                    {
                                        ContactControl objContactControl = new ContactControl(objContact);
                                        // Attach an EventHandler for the ConnectionRatingChanged, LoyaltyRatingChanged, DeleteContact, FileNameChanged Events and OtherCostChanged
                                        objContactControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                        objContactControl.DeleteContact += DeleteContact;
                                        objContactControl.MouseDown += DragContactControl;

                                        panContacts.Controls.Add(objContactControl);
                                    }, token);
                                }
                                    break;

                                case ContactType.Enemy:
                                {
                                    if (panEnemies == null || !CharacterObjectSettings.EnableEnemyTracking)
                                        break;
                                    await this.DoThreadSafeAsync(() =>
                                    {
                                        ContactControl objContactControl = new ContactControl(objContact);
                                        // Attach an EventHandler for the ConnectionRatingChanged, LoyaltyRatingChanged, DeleteContact, FileNameChanged Events and OtherCostChanged
                                        objContactControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                        objContactControl.DeleteContact += DeleteEnemy;
                                        objContactControl.MouseDown += DragContactControl;

                                        panEnemies.Controls.Add(objContactControl);
                                    }, token);
                                }
                                    break;

                                case ContactType.Pet:
                                {
                                    if (panPets == null)
                                        break;
                                    await this.DoThreadSafeAsync(() =>
                                    {
                                        PetControl objContactControl = new PetControl(objContact);
                                        // Attach an EventHandler for the ConnectionRatingChanged, LoyaltyRatingChanged, DeleteContact, FileNameChanged Events and OtherCostChanged
                                        objContactControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                        objContactControl.DeleteContact += DeletePet;
                                        objContactControl.MouseDown += DragContactControl;

                                        panPets.Controls.Add(objContactControl);
                                    }, token);
                                }
                                    break;
                            }
                        }
                    }
                    finally
                    {
                        if (panContacts != null)
                            await panContacts.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                        if (panEnemies != null)
                            await panEnemies.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                        if (panPets != null)
                            await panPets.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (Contact objContact in notifyCollectionChangedEventArgs.NewItems)
                            {
                                switch (objContact.EntityType)
                                {
                                    case ContactType.Contact:
                                    {
                                        if (panContacts == null)
                                            break;
                                        await panContacts.DoThreadSafeAsync(x =>
                                        {
                                            ContactControl objContactControl = new ContactControl(objContact);
                                            // Attach an EventHandler for the ConnectionRatingChanged, LoyaltyRatingChanged, DeleteContact, FileNameChanged Events and OtherCostChanged
                                            objContactControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                            objContactControl.DeleteContact += DeleteContact;
                                            objContactControl.MouseDown += DragContactControl;

                                            x.Controls.Add(objContactControl);
                                        }, token);
                                    }
                                        break;

                                    case ContactType.Enemy:
                                    {
                                        if (panEnemies == null || !CharacterObjectSettings.EnableEnemyTracking)
                                            break;
                                        await panEnemies.DoThreadSafeAsync(x =>
                                        {
                                            ContactControl objContactControl = new ContactControl(objContact);
                                            // Attach an EventHandler for the ConnectionRatingChanged, LoyaltyRatingChanged, DeleteContact, FileNameChanged Events and OtherCostChanged
                                            objContactControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                            objContactControl.DeleteContact += DeleteEnemy;
                                            objContactControl.MouseDown += DragContactControl;

                                            x.Controls.Add(objContactControl);
                                        }, token);
                                    }
                                        break;

                                    case ContactType.Pet:
                                    {
                                        if (panPets == null)
                                            break;
                                        await panPets.DoThreadSafeAsync(x =>
                                        {
                                            PetControl objContactControl = new PetControl(objContact);
                                            // Attach an EventHandler for the ConnectionRatingChanged, LoyaltyRatingChanged, DeleteContact, FileNameChanged Events and OtherCostChanged
                                            objContactControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                            objContactControl.DeleteContact += DeletePet;
                                            objContactControl.MouseDown += DragContactControl;

                                            x.Controls.Add(objContactControl);
                                        }, token);
                                    }
                                        break;
                                }
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (Contact objContact in notifyCollectionChangedEventArgs.OldItems)
                            {
                                switch (objContact.EntityType)
                                {
                                    case ContactType.Contact:
                                    {
                                        if (panContacts == null)
                                            break;
                                        await panContacts.DoThreadSafeAsync(x =>
                                        {
                                            for (int i = x.Controls.Count - 1; i >= 0; i--)
                                            {
                                                if (x.Controls[i] is ContactControl objContactControl &&
                                                    objContactControl.ContactObject == objContact)
                                                {
                                                    x.Controls.RemoveAt(i);
                                                    objContactControl.ContactDetailChanged
                                                        -= MakeDirtyWithCharacterUpdate;
                                                    objContactControl.DeleteContact -= DeleteContact;
                                                    objContactControl.MouseDown -= DragContactControl;
                                                    objContactControl.Dispose();
                                                }
                                            }
                                        }, token);
                                    }
                                        break;

                                    case ContactType.Enemy:
                                    {
                                        if (panEnemies == null)
                                            break;
                                        await panEnemies.DoThreadSafeAsync(x =>
                                        {
                                            for (int i = x.Controls.Count - 1; i >= 0; i--)
                                            {
                                                if (x.Controls[i] is ContactControl objContactControl
                                                    && objContactControl.ContactObject == objContact)
                                                {
                                                    x.Controls.RemoveAt(i);
                                                    objContactControl.ContactDetailChanged
                                                        -= MakeDirtyWithCharacterUpdate;
                                                    objContactControl.DeleteContact -= DeleteEnemy;
                                                    objContactControl.Dispose();
                                                }
                                            }
                                        }, token);
                                    }
                                        break;

                                    case ContactType.Pet:
                                    {
                                        if (panPets == null)
                                            break;
                                        await panPets.DoThreadSafeAsync(x =>
                                        {
                                            for (int i = x.Controls.Count - 1; i >= 0; i--)
                                            {
                                                if (x.Controls[i] is PetControl objPetControl &&
                                                    objPetControl.ContactObject == objContact)
                                                {
                                                    x.Controls.RemoveAt(i);
                                                    objPetControl.ContactDetailChanged
                                                        -= MakeDirtyWithCharacterUpdate;
                                                    objPetControl.DeleteContact -= DeletePet;
                                                    objPetControl.Dispose();
                                                }
                                            }
                                        }, token);
                                    }
                                        break;
                                }
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Replace:
                        {
                            foreach (Contact objContact in notifyCollectionChangedEventArgs.OldItems)
                            {
                                switch (objContact.EntityType)
                                {
                                    case ContactType.Contact:
                                    {
                                        if (panContacts == null)
                                            break;
                                        await panContacts.DoThreadSafeAsync(x =>
                                        {
                                            for (int i = x.Controls.Count - 1; i >= 0; i--)
                                            {
                                                if (x.Controls[i] is ContactControl objContactControl &&
                                                    objContactControl.ContactObject == objContact)
                                                {
                                                    x.Controls.RemoveAt(i);
                                                    objContactControl.ContactDetailChanged
                                                        -= MakeDirtyWithCharacterUpdate;
                                                    objContactControl.DeleteContact -= DeleteContact;
                                                    objContactControl.MouseDown -= DragContactControl;
                                                    objContactControl.Dispose();
                                                }
                                            }
                                        }, token);
                                    }
                                        break;

                                    case ContactType.Enemy:
                                    {
                                        if (panEnemies == null)
                                            break;
                                        await panEnemies.DoThreadSafeAsync(x =>
                                        {
                                            for (int i = x.Controls.Count - 1; i >= 0; i--)
                                            {
                                                if (x.Controls[i] is ContactControl objContactControl
                                                    && objContactControl.ContactObject == objContact)
                                                {
                                                    x.Controls.RemoveAt(i);
                                                    objContactControl.ContactDetailChanged
                                                        -= MakeDirtyWithCharacterUpdate;
                                                    objContactControl.DeleteContact -= DeleteEnemy;
                                                    objContactControl.Dispose();
                                                }
                                            }
                                        }, token);
                                    }
                                        break;

                                    case ContactType.Pet:
                                    {
                                        if (panPets == null)
                                            break;
                                        await panPets.DoThreadSafeAsync(x =>
                                        {
                                            for (int i = x.Controls.Count - 1; i >= 0; i--)
                                            {
                                                if (x.Controls[i] is PetControl objPetControl &&
                                                    objPetControl.ContactObject == objContact)
                                                {
                                                    x.Controls.RemoveAt(i);
                                                    objPetControl.ContactDetailChanged
                                                        -= MakeDirtyWithCharacterUpdate;
                                                    objPetControl.DeleteContact -= DeletePet;
                                                    objPetControl.Dispose();
                                                }
                                            }
                                        }, token);
                                    }
                                        break;
                                }
                            }

                            foreach (Contact objContact in notifyCollectionChangedEventArgs.NewItems)
                            {
                                switch (objContact.EntityType)
                                {
                                    case ContactType.Contact:
                                    {
                                        if (panContacts == null)
                                            break;
                                        await panContacts.DoThreadSafeAsync(x =>
                                        {
                                            ContactControl objContactControl = new ContactControl(objContact);
                                            // Attach an EventHandler for the ConnectionRatingChanged, LoyaltyRatingChanged, DeleteContact, FileNameChanged Events and OtherCostChanged
                                            objContactControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                            objContactControl.DeleteContact += DeleteContact;
                                            objContactControl.MouseDown += DragContactControl;

                                            x.Controls.Add(objContactControl);
                                        }, token);
                                    }
                                        break;

                                    case ContactType.Enemy:
                                    {
                                        if (panEnemies == null || !CharacterObjectSettings.EnableEnemyTracking)
                                            break;
                                        await panEnemies.DoThreadSafeAsync(x =>
                                        {
                                            ContactControl objContactControl = new ContactControl(objContact);
                                            // Attach an EventHandler for the ConnectionRatingChanged, LoyaltyRatingChanged, DeleteContact, FileNameChanged Events and OtherCostChanged
                                            objContactControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                            objContactControl.DeleteContact += DeleteEnemy;
                                            objContactControl.MouseDown += DragContactControl;

                                            x.Controls.Add(objContactControl);
                                        }, token);
                                    }
                                        break;

                                    case ContactType.Pet:
                                    {
                                        if (panPets == null)
                                            break;
                                        await panPets.DoThreadSafeAsync(x =>
                                        {
                                            PetControl objContactControl = new PetControl(objContact);
                                            // Attach an EventHandler for the ConnectionRatingChanged, LoyaltyRatingChanged, DeleteContact, FileNameChanged Events and OtherCostChanged
                                            objContactControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                            objContactControl.DeleteContact += DeletePet;
                                            objContactControl.MouseDown += DragContactControl;

                                            x.Controls.Add(objContactControl);
                                        }, token);
                                    }
                                        break;
                                }
                            }
                        }
                            break;
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        /// <summary>
        /// Refreshes the all panels for sustained objects (spells, complex forms, critter powers)
        /// </summary>
        /// <param name="pnlSustainedSpells">Panel for sustained spells.</param>
        /// <param name="pnlSustainedComplexForms">Panel for sustained complex forms.</param>
        /// <param name="pnlSustainedCritterPowers">Panel for sustained critter powers.</param>
        /// <param name="chkPsycheActiveMagician">Checkbox for Psyche in the tab for spells.</param>
        /// <param name="chkPsycheActiveTechnomancer">Checkbox for Psyche in the tab for complex forms.</param>
        /// <param name="notifyCollectionChangedEventArgs"></param>
        /// <param name="token">Cancellation token to listen to.</param>
        public async ValueTask RefreshSustainedSpells(Panel pnlSustainedSpells, Panel pnlSustainedComplexForms, Panel pnlSustainedCritterPowers, CheckBox chkPsycheActiveMagician, CheckBox chkPsycheActiveTechnomancer, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (pnlSustainedSpells == null && pnlSustainedComplexForms == null && pnlSustainedCritterPowers == null)
                return;

            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                Panel DetermineRefreshingPanel(SustainedObject objSustained, Panel flpSustainedSpellsParam,
                                               Panel flpSustainedComplexFormsParam,
                                               Panel flpSustainedCritterPowersParam)
                {
                    // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                    switch (objSustained.LinkedObjectType)
                    {
                        case Improvement.ImprovementSource.Spell:
                            return flpSustainedSpellsParam;

                        case Improvement.ImprovementSource.ComplexForm:
                            return flpSustainedComplexFormsParam;

                        case Improvement.ImprovementSource.CritterPower:
                            return flpSustainedCritterPowersParam;
                    }

                    return null;
                }

                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    await chkPsycheActiveMagician.DoThreadSafeAsync(x =>
                    {
                        if (x != null)
                            x.Visible = false;
                    }, token);
                    await chkPsycheActiveTechnomancer.DoThreadSafeAsync(x =>
                    {
                        if (x != null)
                            x.Visible = false;
                    }, token);
                    await pnlSustainedSpells.DoThreadSafeAsync(x =>
                    {
                        if (x != null)
                        {
                            x.Controls.Clear();
                            x.Visible = false;
                        }
                    }, token);
                    await pnlSustainedComplexForms.DoThreadSafeAsync(x =>
                    {
                        if (x != null)
                        {
                            x.Controls.Clear();
                            x.Visible = false;
                        }
                    }, token);
                    await pnlSustainedCritterPowers.DoThreadSafeAsync(x =>
                    {
                        if (x != null)
                        {
                            x.Controls.Clear();
                            x.Visible = false;
                        }
                    }, token);
                    foreach (SustainedObject objSustained in CharacterObject.SustainedCollection)
                    {
                        Panel refreshingPanel = DetermineRefreshingPanel(objSustained, pnlSustainedSpells,
                                                                         pnlSustainedComplexForms,
                                                                         pnlSustainedCritterPowers);

                        if (refreshingPanel == null)
                            continue;

                        await refreshingPanel.DoThreadSafeAsync(x =>
                        {
                            x.Visible = true;
                            switch (objSustained.LinkedObjectType)
                            {
                                case Improvement.ImprovementSource.Spell:
                                    chkPsycheActiveMagician.DoThreadSafe(y =>
                                    {
                                        if (y != null)
                                            y.Visible = true;
                                    });
                                    break;

                                case Improvement.ImprovementSource.ComplexForm:
                                    chkPsycheActiveTechnomancer.DoThreadSafe(y =>
                                    {
                                        if (y != null)
                                            y.Visible = true;
                                    });
                                    break;
                            }

                            int intSustainedObjects = x.Controls.Count;

                            SustainedObjectControl objSustainedObjectControl = new SustainedObjectControl(objSustained);

                            objSustainedObjectControl.SustainedObjectDetailChanged += MakeDirtyWithCharacterUpdate;
                            objSustainedObjectControl.UnsustainObject += DeleteSustainedObject;

                            objSustainedObjectControl.Top = intSustainedObjects * objSustainedObjectControl.Height;

                            x.Controls.Add(objSustainedObjectControl);
                        }, token);
                    }
                }
                else
                {
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            foreach (SustainedObject objSustained in notifyCollectionChangedEventArgs.NewItems)
                            {
                                Panel refreshingPanel = DetermineRefreshingPanel(objSustained, pnlSustainedSpells,
                                    pnlSustainedComplexForms, pnlSustainedCritterPowers);

                                if (refreshingPanel == null)
                                    continue;

                                await refreshingPanel.DoThreadSafeAsync(x =>
                                {
                                    x.Visible = true;
                                    switch (objSustained.LinkedObjectType)
                                    {
                                        case Improvement.ImprovementSource.Spell:
                                            chkPsycheActiveMagician.DoThreadSafe(y =>
                                            {
                                                if (y != null)
                                                    y.Visible = true;
                                            });
                                            break;

                                        case Improvement.ImprovementSource.ComplexForm:
                                            chkPsycheActiveTechnomancer.DoThreadSafe(y =>
                                            {
                                                if (y != null)
                                                    y.Visible = true;
                                            });
                                            break;
                                    }

                                    int intSustainedObjects = x.Controls.Count;

                                    SustainedObjectControl objSustainedObjectControl
                                        = new SustainedObjectControl(objSustained);

                                    objSustainedObjectControl.SustainedObjectDetailChanged
                                        += MakeDirtyWithCharacterUpdate;
                                    objSustainedObjectControl.UnsustainObject += DeleteSustainedObject;

                                    objSustainedObjectControl.Top
                                        = intSustainedObjects * objSustainedObjectControl.Height;

                                    x.Controls.Add(objSustainedObjectControl);
                                }, token);
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (SustainedObject objSustained in notifyCollectionChangedEventArgs.OldItems)
                            {
                                Panel refreshingPanel = DetermineRefreshingPanel(objSustained, pnlSustainedSpells,
                                    pnlSustainedComplexForms, pnlSustainedCritterPowers);

                                if (refreshingPanel == null)
                                    continue;

                                int intMoveUpAmount = 0;
                                await refreshingPanel.DoThreadSafeAsync(x =>
                                {
                                    int intSustainedObjects = x.Controls.Count;

                                    for (int i = 0; i < intSustainedObjects; ++i)
                                    {
                                        Control objLoopControl = x.Controls[i];
                                        if (objLoopControl is SustainedObjectControl objSustainedSpellControl &&
                                            objSustainedSpellControl.LinkedSustainedObject == objSustained)
                                        {
                                            intMoveUpAmount = objSustainedSpellControl.Height;

                                            x.Controls.RemoveAt(i);

                                            objSustainedSpellControl.SustainedObjectDetailChanged -=
                                                MakeDirtyWithCharacterUpdate;
                                            objSustainedSpellControl.UnsustainObject -= DeleteSustainedObject;
                                            objSustainedSpellControl.Dispose();
                                            --i;
                                            --intSustainedObjects;
                                        }
                                        else if (intMoveUpAmount != 0)
                                        {
                                            objLoopControl.Top -= intMoveUpAmount;
                                        }
                                    }

                                    if (intSustainedObjects == 0)
                                    {
                                        x.Visible = false;
                                        if (x == pnlSustainedSpells)
                                        {
                                            chkPsycheActiveMagician.DoThreadSafe(y =>
                                            {
                                                if (y != null)
                                                    y.Visible = false;
                                            });
                                        }
                                        else if (x == pnlSustainedComplexForms)
                                        {
                                            chkPsycheActiveTechnomancer.DoThreadSafe(y =>
                                            {
                                                if (y != null)
                                                    y.Visible = false;
                                            });
                                        }
                                    }
                                }, token);
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Replace:
                        {
                            foreach (SustainedObject objSustained in notifyCollectionChangedEventArgs.OldItems)
                            {
                                Panel refreshingPanel = DetermineRefreshingPanel(objSustained, pnlSustainedSpells,
                                    pnlSustainedComplexForms, pnlSustainedCritterPowers);

                                if (refreshingPanel == null)
                                    continue;

                                int intMoveUpAmount = 0;
                                await refreshingPanel.DoThreadSafeAsync(x =>
                                {
                                    int intSustainedObjects = x.Controls.Count;

                                    for (int i = 0; i < intSustainedObjects; ++i)
                                    {
                                        Control objLoopControl = x.Controls[i];
                                        if (objLoopControl is SustainedObjectControl objSustainedSpellControl &&
                                            objSustainedSpellControl.LinkedSustainedObject == objSustained)
                                        {
                                            intMoveUpAmount = objSustainedSpellControl.Height;

                                            x.Controls.RemoveAt(i);

                                            objSustainedSpellControl.SustainedObjectDetailChanged -=
                                                MakeDirtyWithCharacterUpdate;
                                            objSustainedSpellControl.UnsustainObject -= DeleteSustainedObject;
                                            objSustainedSpellControl.Dispose();
                                            --i;
                                            --intSustainedObjects;
                                        }
                                        else if (intMoveUpAmount != 0)
                                        {
                                            objLoopControl.Top -= intMoveUpAmount;
                                        }
                                    }

                                    if (intSustainedObjects == 0)
                                    {
                                        x.Visible = false;
                                        if (x == pnlSustainedSpells)
                                        {
                                            chkPsycheActiveMagician.DoThreadSafe(y =>
                                            {
                                                if (y != null)
                                                    y.Visible = false;
                                            });
                                        }
                                        else if (x == pnlSustainedComplexForms)
                                        {
                                            chkPsycheActiveTechnomancer.DoThreadSafe(y =>
                                            {
                                                if (y != null)
                                                    y.Visible = false;
                                            });
                                        }
                                    }
                                }, token);
                            }

                            foreach (SustainedObject objSustained in notifyCollectionChangedEventArgs.NewItems)
                            {
                                Panel refreshingPanel = DetermineRefreshingPanel(objSustained, pnlSustainedSpells,
                                    pnlSustainedComplexForms, pnlSustainedCritterPowers);

                                if (refreshingPanel == null)
                                    continue;

                                await refreshingPanel.DoThreadSafeAsync(x =>
                                {
                                    x.Visible = true;
                                    switch (objSustained.LinkedObjectType)
                                    {
                                        case Improvement.ImprovementSource.Spell:
                                            chkPsycheActiveMagician.DoThreadSafe(y =>
                                            {
                                                if (y != null)
                                                    y.Visible = true;
                                            });
                                            break;

                                        case Improvement.ImprovementSource.ComplexForm:
                                            chkPsycheActiveTechnomancer.DoThreadSafe(y =>
                                            {
                                                if (y != null)
                                                    y.Visible = true;
                                            });
                                            break;
                                    }

                                    int intSustainedObjects = x.Controls.Count;

                                    SustainedObjectControl objSustainedObjectControl
                                        = new SustainedObjectControl(objSustained);

                                    objSustainedObjectControl.SustainedObjectDetailChanged
                                        += MakeDirtyWithCharacterUpdate;
                                    objSustainedObjectControl.UnsustainObject += DeleteSustainedObject;

                                    objSustainedObjectControl.Top
                                        = intSustainedObjects * objSustainedObjectControl.Height;

                                    x.Controls.Add(objSustainedObjectControl);
                                }, token);
                            }
                        }
                            break;
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        public void DeleteSustainedObject(object sender, EventArgs e)
        {
            if (sender is SustainedObjectControl objSender)
            {
                SustainedObject objSustainedObject = objSender.LinkedSustainedObject;

                if (!CommonFunctions.ConfirmDelete(string.Format(LanguageManager.GetString("Message_DeleteSustainedSpell"), objSustainedObject.CurrentDisplayName)))
                    return;

                CharacterObject.SustainedCollection.Remove(objSustainedObject);
            }
        }

        /// <summary>
        /// Moves a tree node to a specified spot in it's parent node collection.
        /// Will persist between loads if the node's object is an ICanSort
        /// </summary>
        /// <param name="objNode">The item to move</param>
        /// <param name="intNewIndex">The new index in the parent array</param>
        /// <param name="token">Cancellation token to listen to.</param>
        public async Task MoveTreeNode(TreeNode objNode, int intNewIndex, CancellationToken token = default)
        {
            if (!(objNode?.Tag is ICanSort objSortable))
                return;

            TreeView treOwningTree = objNode.TreeView;
            TreeNode objParent = objNode.Parent;
            TreeNodeCollection lstNodes = objParent?.Nodes ?? treOwningTree?.Nodes;

            if (lstNodes == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                List<ICanSort> lstSorted = treOwningTree != null
                    ? await treOwningTree.DoThreadSafeFuncAsync(
                        () => lstNodes.Cast<TreeNode>().Select(n => n.Tag).OfType<ICanSort>().ToList(), token)
                    : lstNodes.Cast<TreeNode>().Select(n => n.Tag).OfType<ICanSort>().ToList();

                // Anything that can't be sorted gets sent to the front of the list, so subtract that number from our new
                // sorting index and make sure we're still inside the array
                intNewIndex = Math.Min(lstSorted.Count - 1,
                                       Math.Max(0, intNewIndex + lstSorted.Count - lstNodes.Count));

                lstSorted.Remove(objSortable);
                lstSorted.Insert(intNewIndex, objSortable);

                // Update the sort field of everything in the array. Doing it this way means we only t
                for (int i = 0; i < lstSorted.Count; ++i)
                {
                    lstSorted[i].SortOrder = i;
                }

                // Sort the actual tree
                if (treOwningTree != null)
                    await treOwningTree.DoThreadSafeAsync(x => x.SortCustomOrder(), token);

                await SetDirty(true, token);
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        /// <summary>
        /// Adds the selected Object and child items to the clipboard as appropriate.
        /// </summary>
        /// <param name="selectedObject"></param>
        public void CopyObject(object selectedObject)
        {
            using (CursorWait.New(this))
            {
                switch (selectedObject)
                {
                    case Armor objCopyArmor:
                        {
                            XmlDocument objCharacterXml = new XmlDocument { XmlResolver = null };
                            using (MemoryStream objStream = new MemoryStream())
                            {
                                using (XmlWriter objWriter = Utils.GetStandardXmlWriter(objStream))
                                {
                                    objWriter.WriteStartDocument();

                                    // </characters>
                                    objWriter.WriteStartElement("character");

                                    objCopyArmor.Save(objWriter);
                                    GlobalSettings.ClipboardContentType = ClipboardContentType.Armor;

                                    if (!objCopyArmor.WeaponID.IsEmptyGuid())
                                    {
                                        // <weapons>
                                        objWriter.WriteStartElement("weapons");
                                        // Copy any Weapon that comes with the Gear.
                                        foreach (Weapon objCopyWeapon in CharacterObject.Weapons.DeepWhere(
                                                     x => x.Children,
                                                     x => x.ParentID == objCopyArmor.InternalId))
                                        {
                                            objCopyWeapon.Save(objWriter);
                                        }

                                        objWriter.WriteEndElement();
                                    }

                                    // </characters>
                                    objWriter.WriteEndElement();

                                    // Finish the document and flush the Writer and Stream.
                                    objWriter.WriteEndDocument();
                                    objWriter.Flush();
                                }

                                // Read the stream.
                                objStream.Position = 0;

                                using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                                using (XmlReader objXmlReader =
                                       XmlReader.Create(objReader, GlobalSettings.SafeXmlReaderSettings))
                                    // Put the stream into an XmlDocument
                                    objCharacterXml.Load(objXmlReader);
                            }

                            GlobalSettings.Clipboard = objCharacterXml;
                            break;
                        }
                    case ArmorMod objCopyArmorMod:
                        {
                            XmlDocument objCharacterXml = new XmlDocument { XmlResolver = null };
                            using (MemoryStream objStream = new MemoryStream())
                            {
                                using (XmlWriter objWriter = Utils.GetStandardXmlWriter(objStream))
                                {
                                    objWriter.WriteStartDocument();

                                    // </characters>
                                    objWriter.WriteStartElement("character");

                                    objCopyArmorMod.Save(objWriter);
                                    GlobalSettings.ClipboardContentType = ClipboardContentType.Armor;

                                    if (!objCopyArmorMod.WeaponID.IsEmptyGuid())
                                    {
                                        // <weapons>
                                        objWriter.WriteStartElement("weapons");
                                        // Copy any Weapon that comes with the Gear.
                                        foreach (Weapon objCopyWeapon in CharacterObject.Weapons.DeepWhere(
                                                     x => x.Children,
                                                     x => x.ParentID == objCopyArmorMod.InternalId))
                                        {
                                            objCopyWeapon.Save(objWriter);
                                        }

                                        objWriter.WriteEndElement();
                                    }

                                    // </characters>
                                    objWriter.WriteEndElement();

                                    // Finish the document and flush the Writer and Stream.
                                    objWriter.WriteEndDocument();
                                    objWriter.Flush();
                                }

                                // Read the stream.
                                objStream.Position = 0;

                                using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                                using (XmlReader objXmlReader =
                                       XmlReader.Create(objReader, GlobalSettings.SafeXmlReaderSettings))
                                    // Put the stream into an XmlDocument
                                    objCharacterXml.Load(objXmlReader);
                            }

                            GlobalSettings.Clipboard = objCharacterXml;
                            break;
                        }
                    case Cyberware objCopyCyberware:
                        {
                            XmlDocument objCharacterXml = new XmlDocument { XmlResolver = null };
                            using (MemoryStream objStream = new MemoryStream())
                            {
                                using (XmlWriter objWriter = Utils.GetStandardXmlWriter(objStream))
                                {
                                    objWriter.WriteStartDocument();

                                    // </characters>
                                    objWriter.WriteStartElement("character");

                                    objCopyCyberware.Save(objWriter);
                                    GlobalSettings.ClipboardContentType = ClipboardContentType.Cyberware;

                                    if (!objCopyCyberware.WeaponID.IsEmptyGuid())
                                    {
                                        // <weapons>
                                        objWriter.WriteStartElement("weapons");
                                        // Copy any Weapon that comes with the Gear.
                                        foreach (Weapon objCopyWeapon in CharacterObject.Weapons.DeepWhere(
                                                     x => x.Children,
                                                     x => x.ParentID == objCopyCyberware.InternalId))
                                        {
                                            objCopyWeapon.Save(objWriter);
                                        }

                                        objWriter.WriteEndElement();
                                    }

                                    if (!objCopyCyberware.VehicleID.IsEmptyGuid())
                                    {
                                        // <vehicles>
                                        objWriter.WriteStartElement("vehicles");
                                        // Copy any Vehicle that comes with the Gear.
                                        foreach (Vehicle objCopyVehicle in CharacterObject.Vehicles.Where(x =>
                                                     x.ParentID == objCopyCyberware.InternalId))
                                        {
                                            objCopyVehicle.Save(objWriter);
                                        }

                                        objWriter.WriteEndElement();
                                    }

                                    // </characters>
                                    objWriter.WriteEndElement();

                                    // Finish the document and flush the Writer and Stream.
                                    objWriter.WriteEndDocument();
                                    objWriter.Flush();
                                }

                                // Read the stream.
                                objStream.Position = 0;

                                using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                                using (XmlReader objXmlReader =
                                       XmlReader.Create(objReader, GlobalSettings.SafeXmlReaderSettings))
                                    // Put the stream into an XmlDocument
                                    objCharacterXml.Load(objXmlReader);
                            }

                            GlobalSettings.Clipboard = objCharacterXml;
                            //Clipboard.SetText(objCharacterXml.OuterXml);
                            break;
                        }
                    case Gear objCopyGear:
                        {
                            XmlDocument objCharacterXml = new XmlDocument { XmlResolver = null };
                            using (MemoryStream objStream = new MemoryStream())
                            {
                                using (XmlWriter objWriter = Utils.GetStandardXmlWriter(objStream))
                                {
                                    objWriter.WriteStartDocument();

                                    // </characters>
                                    objWriter.WriteStartElement("character");

                                    objCopyGear.Save(objWriter);
                                    GlobalSettings.ClipboardContentType = ClipboardContentType.Gear;

                                    if (!objCopyGear.WeaponID.IsEmptyGuid())
                                    {
                                        // <weapons>
                                        objWriter.WriteStartElement("weapons");
                                        // Copy any Weapon that comes with the Gear.
                                        foreach (Weapon objCopyWeapon in CharacterObject.Weapons.DeepWhere(
                                                     x => x.Children,
                                                     x => x.ParentID == objCopyGear.InternalId))
                                        {
                                            objCopyWeapon.Save(objWriter);
                                        }

                                        objWriter.WriteEndElement();
                                    }

                                    // </characters>
                                    objWriter.WriteEndElement();

                                    // Finish the document and flush the Writer and Stream.
                                    objWriter.WriteEndDocument();
                                    objWriter.Flush();
                                }

                                // Read the stream.
                                objStream.Position = 0;

                                using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                                using (XmlReader objXmlReader =
                                       XmlReader.Create(objReader, GlobalSettings.SafeXmlReaderSettings))
                                    // Put the stream into an XmlDocument
                                    objCharacterXml.Load(objXmlReader);
                            }

                            GlobalSettings.Clipboard = objCharacterXml;
                            break;
                        }
                    case Lifestyle objCopyLifestyle:
                        {
                            XmlDocument objCharacterXml = new XmlDocument { XmlResolver = null };
                            using (MemoryStream objStream = new MemoryStream())
                            {
                                using (XmlWriter objWriter = Utils.GetStandardXmlWriter(objStream))
                                {
                                    objWriter.WriteStartDocument();

                                    // </characters>
                                    objWriter.WriteStartElement("character");

                                    objCopyLifestyle.Save(objWriter);

                                    // </characters>
                                    objWriter.WriteEndElement();

                                    // Finish the document and flush the Writer and Stream.
                                    objWriter.WriteEndDocument();
                                    objWriter.Flush();

                                    // Read the stream.
                                    objStream.Position = 0;

                                    using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                                    using (XmlReader objXmlReader =
                                           XmlReader.Create(objReader, GlobalSettings.SafeXmlReaderSettings))
                                        // Put the stream into an XmlDocument
                                        objCharacterXml.Load(objXmlReader);
                                }
                            }

                            GlobalSettings.Clipboard = objCharacterXml;
                            GlobalSettings.ClipboardContentType = ClipboardContentType.Lifestyle;
                            //Clipboard.SetText(objCharacterXml.OuterXml);
                            break;
                        }
                    case Vehicle objCopyVehicle:
                        {
                            XmlDocument objCharacterXml = new XmlDocument { XmlResolver = null };
                            using (MemoryStream objStream = new MemoryStream())
                            {
                                using (XmlWriter objWriter = Utils.GetStandardXmlWriter(objStream))
                                {
                                    objWriter.WriteStartDocument();

                                    // </characters>
                                    objWriter.WriteStartElement("character");

                                    objCopyVehicle.Save(objWriter);

                                    // </characters>
                                    objWriter.WriteEndElement();

                                    // Finish the document and flush the Writer and Stream.
                                    objWriter.WriteEndDocument();
                                    objWriter.Flush();
                                }

                                // Read the stream.
                                objStream.Position = 0;

                                using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                                using (XmlReader objXmlReader =
                                       XmlReader.Create(objReader, GlobalSettings.SafeXmlReaderSettings))
                                    // Put the stream into an XmlDocument
                                    objCharacterXml.Load(objXmlReader);
                            }

                            GlobalSettings.Clipboard = objCharacterXml;
                            GlobalSettings.ClipboardContentType = ClipboardContentType.Vehicle;
                            //Clipboard.SetText(objCharacterXml.OuterXml);
                            break;
                        }
                    case Weapon objCopyWeapon:
                        {
                            // Do not let the user copy Gear or Cyberware Weapons.
                            if (objCopyWeapon.Category == "Gear" || objCopyWeapon.Cyberware)
                                return;

                            XmlDocument objCharacterXml = new XmlDocument { XmlResolver = null };
                            using (MemoryStream objStream = new MemoryStream())
                            {
                                using (XmlWriter objWriter = Utils.GetStandardXmlWriter(objStream))
                                {
                                    objWriter.WriteStartDocument();

                                    // </characters>
                                    objWriter.WriteStartElement("character");

                                    objCopyWeapon.Save(objWriter);

                                    // </characters>
                                    objWriter.WriteEndElement();

                                    // Finish the document and flush the Writer and Stream.
                                    objWriter.WriteEndDocument();
                                    objWriter.Flush();
                                }

                                // Read the stream.
                                objStream.Position = 0;

                                using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                                using (XmlReader objXmlReader =
                                       XmlReader.Create(objReader, GlobalSettings.SafeXmlReaderSettings))
                                    // Put the stream into an XmlDocument
                                    objCharacterXml.Load(objXmlReader);
                            }

                            GlobalSettings.Clipboard = objCharacterXml;
                            GlobalSettings.ClipboardContentType = ClipboardContentType.Weapon;
                            break;
                        }
                    case WeaponAccessory objCopyAccessory:
                        {
                            // Do not let the user copy accessories that are unique to its parent.
                            if (objCopyAccessory.IncludedInWeapon)
                                return;

                            XmlDocument objCharacterXml = new XmlDocument { XmlResolver = null };
                            using (MemoryStream objStream = new MemoryStream())
                            {
                                using (XmlWriter objWriter = Utils.GetStandardXmlWriter(objStream))
                                {
                                    objWriter.WriteStartDocument();

                                    // </characters>
                                    objWriter.WriteStartElement("character");

                                    objCopyAccessory.Save(objWriter);

                                    // </characters>
                                    objWriter.WriteEndElement();

                                    // Finish the document and flush the Writer and Stream.
                                    objWriter.WriteEndDocument();
                                    objWriter.Flush();
                                }

                                // Read the stream.
                                objStream.Position = 0;

                                using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                                using (XmlReader objXmlReader =
                                       XmlReader.Create(objReader, GlobalSettings.SafeXmlReaderSettings))
                                    // Put the stream into an XmlDocument
                                    objCharacterXml.Load(objXmlReader);
                            }

                            GlobalSettings.Clipboard = objCharacterXml;
                            GlobalSettings.ClipboardContentType = ClipboardContentType.WeaponAccessory;
                            break;
                        }
                }
            }
        }

        #region ContactControl Events

        protected void DragContactControl(object sender, MouseEventArgs e)
        {
            if (sender is Control source)
                source.DoDragDrop(new TransportWrapper(source), DragDropEffects.Move);
        }

        protected async ValueTask AddContact(CancellationToken token = default)
        {
            Contact objContact = new Contact(CharacterObject)
            {
                EntityType = ContactType.Contact
            };
            await CharacterObject.Contacts.AddAsync(objContact, token: token);
            await RequestCharacterUpdate(token);
            await SetDirty(true, token);
        }

        protected async void DeleteContact(object sender, EventArgs e)
        {
            try
            {
                if (!(sender is ContactControl objSender))
                    return;
                if (!await CommonFunctions.ConfirmDeleteAsync(await LanguageManager.GetStringAsync("Message_DeleteContact", token: GenericToken), GenericToken))
                    return;

                await CharacterObject.Contacts.RemoveAsync(objSender.ContactObject, token: GenericToken);
                await RequestCharacterUpdate(GenericToken);
                await SetDirty(true, GenericToken);
            }
            catch (OperationCanceledException)
            {
                //swallow this
            }
        }

        #endregion ContactControl Events

        #region PetControl Events

        protected async ValueTask AddPet(CancellationToken token = default)
        {
            Contact objContact = new Contact(CharacterObject)
            {
                EntityType = ContactType.Pet
            };

            await CharacterObject.Contacts.AddAsync(objContact, token: token);
            await RequestCharacterUpdate(token);
            await SetDirty(true, token);
        }

        protected async void DeletePet(object sender, EventArgs e)
        {
            try
            {
                if (!(sender is PetControl objSender))
                    return;
                if (!await CommonFunctions.ConfirmDeleteAsync(await LanguageManager.GetStringAsync("Message_DeleteContact", token: GenericToken), GenericToken))
                    return;

                await CharacterObject.Contacts.RemoveAsync(objSender.ContactObject, token: GenericToken);
                await RequestCharacterUpdate(GenericToken);
                await SetDirty(true, GenericToken);
            }
            catch (OperationCanceledException)
            {
                //swallow this
            }
        }

        #endregion PetControl Events

        #region EnemyControl Events

        protected async ValueTask AddEnemy(CancellationToken token = default)
        {
            // Handle the ConnectionRatingChanged Event for the ContactControl object.
            Contact objContact = new Contact(CharacterObject)
            {
                EntityType = ContactType.Enemy
            };

            await CharacterObject.Contacts.AddAsync(objContact, token: token);
            await RequestCharacterUpdate(token);
            await SetDirty(true, token);
        }

        protected async void DeleteEnemy(object sender, EventArgs e)
        {
            try
            {
                if (!(sender is ContactControl objSender))
                    return;
                if (!await CommonFunctions.ConfirmDeleteAsync(await LanguageManager.GetStringAsync("Message_DeleteEnemy", token: GenericToken), GenericToken))
                    return;

                await CharacterObject.Contacts.RemoveAsync(objSender.ContactObject, token: GenericToken);
                await RequestCharacterUpdate(GenericToken);
                await SetDirty(true, GenericToken);
            }
            catch (OperationCanceledException)
            {
                //swallow this
            }
        }

        #endregion EnemyControl Events

        #region Additional Relationships Tab Control Events

        protected async ValueTask AddContactsFromFile(CancellationToken token = default)
        {
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token).ConfigureAwait(false);
            try
            {
                XPathDocument xmlDoc;
                string strFileName = string.Empty;
                string strFilter = await LanguageManager.GetStringAsync("DialogFilter_Xml", token: token)
                                                        .ConfigureAwait(false) + '|' +
                                   await LanguageManager.GetStringAsync("DialogFilter_All", token: token)
                                                        .ConfigureAwait(false);
                // Displays an OpenFileDialog so the user can select the XML to read.
                DialogResult eResult = await this.DoThreadSafeFuncAsync(x =>
                {
                    using (OpenFileDialog dlgOpenFile = new OpenFileDialog())
                    {
                        dlgOpenFile.Filter = strFilter;
                        // Show the Dialog.
                        DialogResult eReturn = dlgOpenFile.ShowDialog(x);
                        strFileName = dlgOpenFile.FileName;
                        return eReturn;
                    }
                }, token);
                // If the user cancels out, return early.
                if (eResult != DialogResult.OK)
                    return;
                try
                {
                    xmlDoc = await XPathDocumentExtensions.LoadStandardFromFileAsync(strFileName, token: token).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    Program.ShowMessageBox(this, ex.ToString());
                    return;
                }
                catch (XmlException ex)
                {
                    Program.ShowMessageBox(this, ex.ToString());
                    return;
                }

                foreach (XPathNavigator xmlContact in await xmlDoc.CreateNavigator()
                                                                  .SelectAndCacheExpressionAsync(
                                                                      "/chummer/contacts/contact", token: token).ConfigureAwait(false))
                {
                    Contact objContact = new Contact(CharacterObject);
                    objContact.Load(xmlContact);
                    await CharacterObject.Contacts.AddAsync(objContact, token).ConfigureAwait(false);
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync().ConfigureAwait(false);
            }
        }

        #endregion Additional Relationships Tab Control Events

        public async ValueTask RefreshSpirits(Panel panSpirits, Panel panSprites, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs = null, CancellationToken token = default)
        {
            if (panSpirits == null && panSprites == null)
                return;
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                if (notifyCollectionChangedEventArgs == null ||
                    notifyCollectionChangedEventArgs.Action == NotifyCollectionChangedAction.Reset)
                {
                    if (panSpirits != null)
                        await panSpirits.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    if (panSprites != null)
                        await panSprites.DoThreadSafeAsync(x => x.SuspendLayout(), token);
                    try
                    {
                        if (panSpirits != null)
                            await panSpirits.DoThreadSafeAsync(x => x.Controls.Clear(), token);
                        if (panSprites != null)
                            await panSprites.DoThreadSafeAsync(x => x.Controls.Clear(), token);
                        int intSpirits = -1;
                        int intSprites = -1;
                        foreach (Spirit objSpirit in CharacterObject.Spirits)
                        {
                            bool blnIsSpirit = objSpirit.EntityType == SpiritType.Spirit;
                            if (blnIsSpirit)
                            {
                                if (panSpirits == null)
                                    continue;
                            }
                            else if (panSprites == null)
                                continue;

                            SpiritControl objSpiritControl
                                = await this.DoThreadSafeFuncAsync(() => new SpiritControl(objSpirit), token);

                            // Attach an EventHandler for the ServicesOwedChanged Event.
                            objSpiritControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                            objSpiritControl.DeleteSpirit += DeleteSpirit;

                            await objSpiritControl.RebuildSpiritList(CharacterObject.MagicTradition, token);

                            if (blnIsSpirit)
                            {
                                ++intSpirits;
                                await objSpiritControl.DoThreadSafeAsync(
                                    x => x.Top = intSpirits * x.Height, token);
                                await panSpirits.DoThreadSafeAsync(x => x.Controls.Add(objSpiritControl), token);
                            }
                            else
                            {
                                ++intSprites;
                                await objSpiritControl.DoThreadSafeAsync(
                                    x => x.Top = intSprites * x.Height, token);
                                await panSprites.DoThreadSafeAsync(x => x.Controls.Add(objSpiritControl), token);
                            }
                        }
                    }
                    finally
                    {
                        if (panSpirits != null)
                            await panSpirits.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                        if (panSprites != null)
                            await panSprites.DoThreadSafeAsync(x => x.ResumeLayout(), token);
                    }
                }
                else
                {
                    switch (notifyCollectionChangedEventArgs.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                        {
                            int intSpirits = panSpirits != null
                                ? await panSpirits.DoThreadSafeFuncAsync(x => x.Controls.Count, token)
                                : 0;
                            int intSprites = panSprites != null
                                ? await panSprites.DoThreadSafeFuncAsync(x => x.Controls.Count, token)
                                : 0;
                            foreach (Spirit objSpirit in notifyCollectionChangedEventArgs.NewItems)
                            {
                                bool blnIsSpirit = objSpirit.EntityType == SpiritType.Spirit;
                                if (blnIsSpirit)
                                {
                                    if (panSpirits == null)
                                        continue;
                                }
                                else if (panSprites == null)
                                    continue;

                                SpiritControl objSpiritControl
                                    = await this.DoThreadSafeFuncAsync(() => new SpiritControl(objSpirit),
                                                                       token);

                                // Attach an EventHandler for the ServicesOwedChanged Event.
                                objSpiritControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                objSpiritControl.DeleteSpirit += DeleteSpirit;

                                await objSpiritControl.RebuildSpiritList(CharacterObject.MagicTradition, token);

                                if (blnIsSpirit)
                                {
                                    await objSpiritControl.DoThreadSafeAsync(
                                        x => x.Top = intSpirits * x.Height, token);
                                    await panSpirits.DoThreadSafeAsync(x => x.Controls.Add(objSpiritControl),
                                                                       token);
                                    ++intSpirits;
                                }
                                else
                                {
                                    await objSpiritControl.DoThreadSafeAsync(
                                        x => x.Top = intSprites * x.Height, token);
                                    await panSprites.DoThreadSafeAsync(x => x.Controls.Add(objSpiritControl),
                                                                       token);
                                    ++intSprites;
                                }
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Remove:
                        {
                            foreach (Spirit objSpirit in notifyCollectionChangedEventArgs.OldItems)
                            {
                                int intMoveUpAmount = 0;
                                if (objSpirit.EntityType == SpiritType.Spirit)
                                {
                                    if (panSpirits == null)
                                        continue;
                                    int intSpirits
                                        = await panSpirits.DoThreadSafeFuncAsync(x => x.Controls.Count, token);
                                    for (int i = 0; i < intSpirits; ++i)
                                    {
                                        Control objLoopControl
                                            = await panSpirits.DoThreadSafeFuncAsync(x => x.Controls[i], token);
                                        if (objLoopControl is SpiritControl objSpiritControl &&
                                            objSpiritControl.SpiritObject == objSpirit)
                                        {
                                            intMoveUpAmount
                                                = await objSpiritControl.DoThreadSafeFuncAsync(
                                                    x => x.Height, token);
                                            await panSpirits.DoThreadSafeAsync(
                                                x => x.Controls.RemoveAt(i), token);
                                            await objSpiritControl.DoThreadSafeAsync(x =>
                                            {
                                                x.ContactDetailChanged
                                                    -= MakeDirtyWithCharacterUpdate;
                                                x.DeleteSpirit -= DeleteSpirit;
                                                x.Dispose();
                                            }, token);
                                            --i;
                                            --intSpirits;
                                        }
                                        else if (intMoveUpAmount != 0)
                                        {
                                            await objLoopControl.DoThreadSafeAsync(
                                                x => x.Top -= intMoveUpAmount, token);
                                        }
                                    }
                                }
                                else if (panSprites != null)
                                {
                                    int intSprites = panSprites.Controls.Count;
                                    for (int i = 0; i < intSprites; ++i)
                                    {
                                        Control objLoopControl = panSprites.Controls[i];
                                        if (objLoopControl is SpiritControl objSpiritControl &&
                                            objSpiritControl.SpiritObject == objSpirit)
                                        {
                                            intMoveUpAmount
                                                = await objSpiritControl.DoThreadSafeFuncAsync(
                                                    x => x.Height, token);
                                            await panSprites.DoThreadSafeAsync(
                                                x => x.Controls.RemoveAt(i), token);
                                            await objSpiritControl.DoThreadSafeAsync(x =>
                                            {
                                                x.ContactDetailChanged
                                                    -= MakeDirtyWithCharacterUpdate;
                                                x.DeleteSpirit -= DeleteSpirit;
                                                x.Dispose();
                                            }, token);
                                            --i;
                                            --intSprites;
                                        }
                                        else if (intMoveUpAmount != 0)
                                        {
                                            await objLoopControl.DoThreadSafeAsync(
                                                x => x.Top -= intMoveUpAmount, token);
                                        }
                                    }
                                }
                            }
                        }
                            break;

                        case NotifyCollectionChangedAction.Replace:
                        {
                            int intSpirits = panSpirits != null
                                ? await panSpirits.DoThreadSafeFuncAsync(x => x.Controls.Count, token)
                                : 0;
                            int intSprites = panSprites != null
                                ? await panSprites.DoThreadSafeFuncAsync(x => x.Controls.Count, token)
                                : 0;
                            foreach (Spirit objSpirit in notifyCollectionChangedEventArgs.OldItems)
                            {
                                int intMoveUpAmount = 0;
                                if (objSpirit.EntityType == SpiritType.Spirit)
                                {
                                    if (panSpirits == null)
                                        continue;
                                    for (int i = 0; i < intSpirits; ++i)
                                    {
                                        Control objLoopControl
                                            = await panSpirits.DoThreadSafeFuncAsync(x => x.Controls[i], token);
                                        if (objLoopControl is SpiritControl objSpiritControl &&
                                            objSpiritControl.SpiritObject == objSpirit)
                                        {
                                            intMoveUpAmount
                                                = await objSpiritControl.DoThreadSafeFuncAsync(
                                                    x => x.Height, token);
                                            await panSpirits.DoThreadSafeAsync(
                                                x => x.Controls.RemoveAt(i), token);
                                            await objSpiritControl.DoThreadSafeAsync(x =>
                                            {
                                                x.ContactDetailChanged
                                                    -= MakeDirtyWithCharacterUpdate;
                                                x.DeleteSpirit -= DeleteSpirit;
                                                x.Dispose();
                                            }, token);
                                            --i;
                                            --intSpirits;
                                        }
                                        else if (intMoveUpAmount != 0)
                                        {
                                            await objLoopControl.DoThreadSafeAsync(
                                                x => x.Top -= intMoveUpAmount, token);
                                        }
                                    }
                                }
                                else if (panSprites != null)
                                {
                                    for (int i = 0; i < intSprites; ++i)
                                    {
                                        Control objLoopControl = panSprites.Controls[i];
                                        if (objLoopControl is SpiritControl objSpiritControl &&
                                            objSpiritControl.SpiritObject == objSpirit)
                                        {
                                            intMoveUpAmount
                                                = await objSpiritControl.DoThreadSafeFuncAsync(
                                                    x => x.Height, token);
                                            await panSprites.DoThreadSafeAsync(
                                                x => x.Controls.RemoveAt(i), token);
                                            await objSpiritControl.DoThreadSafeAsync(x =>
                                            {
                                                x.ContactDetailChanged
                                                    -= MakeDirtyWithCharacterUpdate;
                                                x.DeleteSpirit -= DeleteSpirit;
                                                x.Dispose();
                                            }, token);
                                            --i;
                                            --intSprites;
                                        }
                                        else if (intMoveUpAmount != 0)
                                        {
                                            await objLoopControl.DoThreadSafeAsync(
                                                x => x.Top -= intMoveUpAmount, token);
                                        }
                                    }
                                }
                            }

                            foreach (Spirit objSpirit in notifyCollectionChangedEventArgs.NewItems)
                            {
                                bool blnIsSpirit = objSpirit.EntityType == SpiritType.Spirit;
                                if (blnIsSpirit)
                                {
                                    if (panSpirits == null)
                                        continue;
                                }
                                else if (panSprites == null)
                                    continue;

                                SpiritControl objSpiritControl
                                    = await this.DoThreadSafeFuncAsync(() => new SpiritControl(objSpirit),
                                                                       token);

                                // Attach an EventHandler for the ServicesOwedChanged Event.
                                objSpiritControl.ContactDetailChanged += MakeDirtyWithCharacterUpdate;
                                objSpiritControl.DeleteSpirit += DeleteSpirit;

                                await objSpiritControl.RebuildSpiritList(CharacterObject.MagicTradition, token);

                                if (blnIsSpirit)
                                {
                                    await objSpiritControl.DoThreadSafeAsync(
                                        x => x.Top = intSpirits * x.Height, token);
                                    await panSpirits.DoThreadSafeAsync(x => x.Controls.Add(objSpiritControl),
                                                                       token);
                                    ++intSpirits;
                                }
                                else
                                {
                                    await objSpiritControl.DoThreadSafeAsync(
                                        x => x.Top = intSprites * x.Height, token);
                                    await panSprites.DoThreadSafeAsync(x => x.Controls.Add(objSpiritControl),
                                                                       token);
                                    ++intSprites;
                                }
                            }
                        }
                            break;
                    }
                }
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        #region SpiritControl Events

        protected async ValueTask AddSpirit(CancellationToken token = default)
        {
            // The number of bound Spirits cannot exceed the character's CHA.
            if (!CharacterObject.IgnoreRules && CharacterObject.Spirits.Count(x => x.EntityType == SpiritType.Spirit && x.Bound && !x.Fettered) >= CharacterObject.BoundSpiritLimit)
            {
                Program.ShowMessageBox(
                    this,
                    string.Format(GlobalSettings.CultureInfo, await LanguageManager.GetStringAsync("Message_BoundSpiritLimit", token: token),
                                  CharacterObject.Settings.BoundSpiritExpression, CharacterObject.BoundSpiritLimit),
                    await LanguageManager.GetStringAsync("MessageTitle_BoundSpiritLimit", token: token),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Spirit objSpirit = new Spirit(CharacterObject)
            {
                EntityType = SpiritType.Spirit,
                Force = CharacterObject.MaxSpiritForce
            };
            await CharacterObject.Spirits.AddAsync(objSpirit, token: token);
            await RequestCharacterUpdate(token);
            await SetDirty(true, token);
        }

        protected async ValueTask AddSprite(CancellationToken token = default)
        {
            // In create, all sprites are added as Bound/Registered. The number of registered Sprites cannot exceed the character's LOG.
            if (!CharacterObject.IgnoreRules &&
                CharacterObject.Spirits.Count(x => x.EntityType == SpiritType.Sprite && x.Bound && !x.Fettered) >=
                CharacterObject.RegisteredSpriteLimit)
            {
                Program.ShowMessageBox(
                    this,
                    string.Format(GlobalSettings.CultureInfo,
                                  await LanguageManager.GetStringAsync("Message_RegisteredSpriteLimit", token: token),
                                  CharacterObject.Settings.RegisteredSpriteExpression,
                                  CharacterObject.RegisteredSpriteLimit),
                    await LanguageManager.GetStringAsync("MessageTitle_RegisteredSpriteLimit", token: token),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Spirit objSprite = new Spirit(CharacterObject)
            {
                EntityType = SpiritType.Sprite,
                Force = CharacterObject.MaxSpriteLevel
            };
            await CharacterObject.Spirits.AddAsync(objSprite, token: token);
            await RequestCharacterUpdate(token);
            await SetDirty(true, token);
        }

        protected async void DeleteSpirit(object sender, EventArgs e)
        {
            try
            {
                if (!(sender is SpiritControl objSender))
                    return;
                Spirit objSpirit = objSender.SpiritObject;
                bool blnIsSpirit = objSpirit.EntityType == SpiritType.Spirit;
                if (!await CommonFunctions.ConfirmDeleteAsync(await LanguageManager.GetStringAsync(blnIsSpirit ? "Message_DeleteSpirit" : "Message_DeleteSprite", token: GenericToken), GenericToken))
                    return;
                objSpirit.Fettered = false; // Fettered spirits consume MAG.
                await CharacterObject.Spirits.RemoveAsync(objSpirit, token: GenericToken);
                await RequestCharacterUpdate(GenericToken);
                await SetDirty(true, GenericToken);
            }
            catch (OperationCanceledException)
            {
                //swallow this
            }
        }

        #endregion SpiritControl Events

        /// <summary>
        /// Add a mugshot to the character.
        /// </summary>
        protected async ValueTask<bool> AddMugshot(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                using (OpenFileDialog dlgOpenFile = await this.DoThreadSafeFuncAsync(() => new OpenFileDialog(), token))
                {
                    if (!string.IsNullOrWhiteSpace(GlobalSettings.RecentImageFolder) &&
                        Directory.Exists(GlobalSettings.RecentImageFolder))
                    {
                        dlgOpenFile.InitialDirectory = GlobalSettings.RecentImageFolder;
                    }
                    // Prompt the user to select an image to associate with this character.

                    ImageCodecInfo[] lstCodecs = ImageCodecInfo.GetImageEncoders();
                    string strFormat = "{0}" + await LanguageManager.GetStringAsync("String_Space", token: token) + "({1})|{1}";
                    dlgOpenFile.Filter = string.Format(
                        GlobalSettings.InvariantCultureInfo,
                        await LanguageManager.GetStringAsync("DialogFilter_ImagesPrefix", token: token) + "({1})|{1}|{0}|" +
                        await LanguageManager.GetStringAsync("DialogFilter_All", token: token),
                        string.Join("|",
                                    lstCodecs.Select(codec => string.Format(GlobalSettings.CultureInfo,
                                                                            strFormat, codec.CodecName,
                                                                            codec.FilenameExtension))),
                        string.Join(";", lstCodecs.Select(codec => codec.FilenameExtension)));

                    bool blnMakeLoop = true;
                    while (blnMakeLoop)
                    {
                        token.ThrowIfCancellationRequested();
                        blnMakeLoop = false;
                        if (await this.DoThreadSafeFuncAsync(x => dlgOpenFile.ShowDialog(x), token) != DialogResult.OK)
                            return false;
                        if (!File.Exists(dlgOpenFile.FileName))
                        {
                            Program.ShowMessageBox(string.Format(
                                                       await LanguageManager.GetStringAsync(
                                                           "Message_File_Cannot_Be_Read_Accessed", token: token),
                                                       dlgOpenFile.FileName));
                            blnMakeLoop = true;
                        }
                    }

                    // Convert the image to a string using Base64.
                    GlobalSettings.RecentImageFolder = Path.GetDirectoryName(dlgOpenFile.FileName);

                    using (Bitmap bmpMugshot = new Bitmap(dlgOpenFile.FileName, true))
                    {
                        if (bmpMugshot.PixelFormat == PixelFormat.Format32bppPArgb)
                        {
                            await CharacterObject.Mugshots.AddAsync(
                                bmpMugshot.Clone() as Bitmap, token); // Clone makes sure file handle is closed
                        }
                        else
                        {
                            await CharacterObject.Mugshots.AddAsync(
                                bmpMugshot.ConvertPixelFormat(PixelFormat.Format32bppPArgb), token);
                        }
                    }

                    if (CharacterObject.MainMugshotIndex == -1)
                        CharacterObject.MainMugshotIndex = CharacterObject.Mugshots.Count - 1;
                }

                return true;
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        /// <summary>
        /// Update the mugshot info of a character.
        /// </summary>
        /// <param name="picMugshot"></param>
        /// <param name="intCurrentMugshotIndexInList"></param>
        protected void UpdateMugshot(PictureBox picMugshot, int intCurrentMugshotIndexInList)
        {
            if (picMugshot == null)
                return;
            if (intCurrentMugshotIndexInList < 0 || intCurrentMugshotIndexInList >= CharacterObject.Mugshots.Count || CharacterObject.Mugshots[intCurrentMugshotIndexInList] == null)
            {
                picMugshot.Image = null;
                return;
            }

            Image imgMugshot = CharacterObject.Mugshots[intCurrentMugshotIndexInList];

            try
            {
                picMugshot.SizeMode = imgMugshot != null && picMugshot.Height >= imgMugshot.Height && picMugshot.Width >= imgMugshot.Width
                    ? PictureBoxSizeMode.CenterImage
                    : PictureBoxSizeMode.Zoom;
            }
            catch (ArgumentException) // No other way to catch when the Image is not null, but is disposed
            {
                picMugshot.SizeMode = PictureBoxSizeMode.Zoom;
            }
            picMugshot.Image = imgMugshot;
        }

        /// <summary>
        /// Remove a mugshot of a character.
        /// </summary>
        /// <param name="intCurrentMugshotIndexInList"></param>
        protected void RemoveMugshot(int intCurrentMugshotIndexInList)
        {
            if (intCurrentMugshotIndexInList < 0 || intCurrentMugshotIndexInList >= CharacterObject.Mugshots.Count)
            {
                return;
            }

            CharacterObject.Mugshots.RemoveAt(intCurrentMugshotIndexInList);
            if (intCurrentMugshotIndexInList == CharacterObject.MainMugshotIndex)
            {
                CharacterObject.MainMugshotIndex = -1;
            }
            else if (intCurrentMugshotIndexInList < CharacterObject.MainMugshotIndex)
            {
                --CharacterObject.MainMugshotIndex;
            }
        }

        protected enum ItemTreeViewTypes
        {
            Misc,
            Weapons,
            Armor,
            Gear,
            Vehicles,
            Improvements
        }

        protected MouseButtons DragButton { get; set; } = MouseButtons.None;
        protected bool DraggingGear { get; set; }

        protected async ValueTask DoTreeDragDrop(object sender, DragEventArgs e, TreeView treView, ItemTreeViewTypes eType, CancellationToken token = default)
        {
            Point pt = ((TreeView)sender).PointToClient(new Point(e.X, e.Y));
            TreeNode nodDestination = await ((TreeView)sender).DoThreadSafeFuncAsync(x => x.GetNodeAt(pt), token);

            TreeNode objSelected = await treView.DoThreadSafeFuncAsync(x => x.SelectedNode, token);
            for (TreeNode nodLoop = nodDestination; nodLoop != null; nodLoop = nodLoop.Parent)
            {
                if (nodLoop == objSelected)
                    return;
            }

            int intNewIndex = 0;
            if (nodDestination != null)
            {
                intNewIndex = nodDestination.Index;
            }
            else
            {
                int intNodesCount = await treView.DoThreadSafeFuncAsync(x => x.Nodes.Count, token);
                if (intNodesCount > 0)
                {
                    await treView.DoThreadSafeAsync(x =>
                    {
                        intNewIndex = x.Nodes[intNodesCount - 1].Nodes.Count;
                        nodDestination = x.Nodes[intNodesCount - 1];
                    }, token);
                }
            }

            // Put the weapon in the right location (or lack thereof)
            await treView.DoThreadSafeAsync(() =>
            {
                switch (eType)
                {
                    case ItemTreeViewTypes.Misc:
                        break;
                    case ItemTreeViewTypes.Weapons:
                    {
                        if (objSelected.Level == 1)
                            CharacterObject.MoveWeaponNode(intNewIndex, nodDestination, objSelected, token: token);
                        else
                            CharacterObject.MoveWeaponRoot(intNewIndex, nodDestination, objSelected);
                        break;
                    }
                    case ItemTreeViewTypes.Armor:
                    {
                        if (objSelected.Level == 1)
                            CharacterObject.MoveArmorNode(intNewIndex, nodDestination, objSelected, token: token);
                        else
                            CharacterObject.MoveArmorRoot(intNewIndex, nodDestination, objSelected);
                        break;
                    }
                    case ItemTreeViewTypes.Gear:
                    {
                        switch (DragButton)
                        {
                            // If the item was moved using the left mouse button, change the order of things.
                            case MouseButtons.Left when objSelected.Level == 1:
                                CharacterObject.MoveGearNode(intNewIndex, nodDestination, objSelected, token: token);
                                break;

                            case MouseButtons.Left:
                                CharacterObject.MoveGearRoot(intNewIndex, nodDestination, objSelected);
                                break;

                            case MouseButtons.Right:
                                CharacterObject.MoveGearParent(objSelected, objSelected, token: token);
                                break;
                        }
                        break;
                    }
                    case ItemTreeViewTypes.Vehicles:
                    {
                        if (!DraggingGear)
                        {
                            CharacterObject.MoveVehicleNode(intNewIndex, nodDestination, objSelected, token: token);
                        }
                        else
                        {
                            CharacterObject.MoveVehicleGearParent(nodDestination, objSelected, token: token);
                            DraggingGear = false;
                        }
                        break;
                    }
                    case ItemTreeViewTypes.Improvements:
                    {
                        if (objSelected.Level == 1)
                            CharacterObject.MoveImprovementNode(nodDestination, objSelected, token: token);
                        else
                            CharacterObject.MoveImprovementRoot(intNewIndex, nodDestination, objSelected);
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(eType), eType, null);
                }
            }, token);

            // Put the weapon in the right order in the tree
            await MoveTreeNode(await treView.DoThreadSafeFuncAsync(x => x.FindNodeByTag(objSelected.Tag), token), intNewIndex, token);

            await treView.DoThreadSafeAsync(x =>
            {
                // Update the entire tree to prevent any holes in the sort order
                x.CacheSortOrder();
                // Clear the background color for all Nodes.
                x.ClearNodeBackground(null);
                // Store our new order so it's loaded properly the next time we open the character
                x.CacheSortOrder();
            }, token);
            
            await SetDirty(true, token);
        }

        /// <summary>
        /// Whether or not the character has changes that can be saved
        /// </summary>
        public bool IsDirty
        {
            get => _blnIsDirty;
            set
            {
                if (_blnIsDirty == value)
                    return;
                _blnIsDirty = value;
                UpdateWindowTitle(true);
            }
        }

        public Task SetDirty(bool blnValue, CancellationToken token = default)
        {
            if (_blnIsDirty == blnValue)
                return Task.CompletedTask;
            _blnIsDirty = blnValue;
            return UpdateWindowTitleAsync(true, token);
        }

        /// <summary>
        /// Whether or not the form is currently in the middle of refreshing some UI elements
        /// </summary>
        public bool IsRefreshing
        {
            get => _intRefreshingCount > 0;
            set
            {
                if (value)
                    Interlocked.Increment(ref _intRefreshingCount);
                else
                {
                    int intCurrentRefreshingCount = Interlocked.Decrement(ref _intRefreshingCount);
                    if (intCurrentRefreshingCount < 0)
                        Interlocked.CompareExchange(ref _intRefreshingCount, 0, intCurrentRefreshingCount);
                }
            }
        }

        public bool IsLoading
        {
            get => _intLoadingCount > 0;
            set
            {
                if (value)
                    Interlocked.Increment(ref _intLoadingCount);
                else
                {
                    int intCurrentLoadingCount = Interlocked.Decrement(ref _intLoadingCount);
                    if (intCurrentLoadingCount < 0)
                        Interlocked.CompareExchange(ref _intLoadingCount, 0, intCurrentLoadingCount);
                }
            }
        }

        public bool IsFinishedInitializing { get; protected set; }

        public async void MakeDirtyWithCharacterUpdate(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Move)
                return;

            try
            {
                await RequestCharacterUpdate(GenericToken);
                await SetDirty(true, GenericToken);
            }
            catch (OperationCanceledException)
            {
                // swallow this
            }
        }

        public async void MakeDirtyWithCharacterUpdate(object sender, ListChangedEventArgs e)
        {
            if (e.ListChangedType != ListChangedType.ItemAdded
                && e.ListChangedType != ListChangedType.ItemChanged
                && e.ListChangedType != ListChangedType.ItemDeleted
                && e.ListChangedType != ListChangedType.Reset)
                return;

            try
            {
                await RequestCharacterUpdate(GenericToken);
                await SetDirty(true, GenericToken);
            }
            catch (OperationCanceledException)
            {
                // swallow this
            }
        }

        public async void MakeDirtyWithCharacterUpdate(object sender, EventArgs e)
        {
            try
            {
                await RequestCharacterUpdate(GenericToken);
                await SetDirty(true, GenericToken);
            }
            catch (OperationCanceledException)
            {
                // swallow this
            }
        }

        public async void MakeDirty(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Move)
                return;

            try
            {
                await SetDirty(true, GenericToken);
            }
            catch (OperationCanceledException)
            {
                // swallow this
            }
        }

        public async void MakeDirty(object sender, EventArgs e)
        {
            try
            {
                await SetDirty(true, GenericToken);
            }
            catch (OperationCanceledException)
            {
                // swallow this
            }
        }

        public async ValueTask RequestCharacterUpdate(CancellationToken token = default)
        {
            if (IsLoading)
                return;
            CancellationTokenSource objSource = null;
            if (token != GenericToken)
            {
                objSource = CancellationTokenSource.CreateLinkedTokenSource(token, GenericToken);
                token = objSource.Token;
            }
            try
            {
                token.ThrowIfCancellationRequested();
                if (!await _objUpdateCharacterInfoSemaphoreSlim.WaitAsync(0, token))
                    return;
                try
                {
                    GenericToken.ThrowIfCancellationRequested();
                    if (_objUpdateCharacterInfoCancellationTokenSource != null)
                    {
                        if (!_objUpdateCharacterInfoCancellationTokenSource.IsCancellationRequested)
                        {
                            try
                            {
                                _objUpdateCharacterInfoCancellationTokenSource.Cancel(false);
                            }
                            catch (ObjectDisposedException)
                            {
                                //swallow this
                            }
                        }

                        _objUpdateCharacterInfoCancellationTokenSource.Dispose();
                        _objUpdateCharacterInfoCancellationTokenSource = null;
                    }

                    token.ThrowIfCancellationRequested();
                    if (_tskUpdateCharacterInfo != null)
                    {
                        try
                        {
                            await _tskUpdateCharacterInfo;
                        }
                        catch (OperationCanceledException)
                        {
                            //swallow this
                        }
                    }

                    token.ThrowIfCancellationRequested();
                    _objUpdateCharacterInfoCancellationTokenSource = new CancellationTokenSource();
                    CancellationToken objToken = _objUpdateCharacterInfoCancellationTokenSource.Token;
                    _tskUpdateCharacterInfo = Task.Run(() => DoUpdateCharacterInfo(objToken), objToken);
                }
                finally
                {
                    _objUpdateCharacterInfoSemaphoreSlim.Release();
                }
            }
            finally
            {
                objSource?.Dispose();
            }
        }

        public bool IsCharacterUpdateRequested => _objUpdateCharacterInfoSemaphoreSlim.CurrentCount == 0
                                                  || _tskUpdateCharacterInfo?.IsCompleted == false;

        protected Task UpdateCharacterInfoTask => _tskUpdateCharacterInfo;

        private Task _tskUpdateCharacterInfo = Task.CompletedTask;

        private readonly DebuggableSemaphoreSlim _objUpdateCharacterInfoSemaphoreSlim = new DebuggableSemaphoreSlim();

        private CancellationTokenSource _objUpdateCharacterInfoCancellationTokenSource;

        protected virtual Task DoUpdateCharacterInfo(CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        protected bool SkipUpdate
        {
            get => _intUpdatingCount > 0;
            set
            {
                if (value)
                    Interlocked.Increment(ref _intUpdatingCount);
                else
                {
                    int intCurrentUpdatingCount = Interlocked.Decrement(ref _intUpdatingCount);
                    if (intCurrentUpdatingCount < 0)
                        Interlocked.CompareExchange(ref _intUpdatingCount, 0, intCurrentUpdatingCount);
                }
            }
        }

        public Character CharacterObject => _objCharacter;

        public IEnumerable<Character> CharacterObjects => _objCharacter?.Yield() ?? Enumerable.Empty<Character>();

        private CharacterSettings _objCachedSettings;

        protected CharacterSettings CharacterObjectSettings => _objCachedSettings ?? (_objCachedSettings = CharacterObject?.Settings);

        protected virtual string FormMode => string.Empty;

        protected void ShiftTabsOnMouseScroll(object sender, MouseEventArgs e)
        {
            if (e == null)
                return;
            //TODO: Global option to switch behaviour on/off, method to emulate clicking the scroll buttons instead of changing the selected index,
            //allow wrapping back to first/last tab item based on scroll direction
            if (sender is TabControl tabControl && e.Location.Y <= tabControl.ItemSize.Height)
            {
                int intScrollAmount = e.Delta;
                int intSelectedTabIndex = tabControl.SelectedIndex;

                if (intScrollAmount < 0)
                {
                    if (intSelectedTabIndex < tabControl.TabCount - 1)
                        tabControl.SelectedIndex = intSelectedTabIndex + 1;
                }
                else if (intSelectedTabIndex > 0)
                    tabControl.SelectedIndex = intSelectedTabIndex - 1;
            }
        }

        /// <summary>
        /// Update the Window title to show the Character's name and unsaved changes status.
        /// </summary>
        protected void UpdateWindowTitle(bool blnCanSkip)
        {
            if (Text.EndsWith('*') == _blnIsDirty && blnCanSkip)
                return;

            string strSpace = LanguageManager.GetString("String_Space");
            string strTitle = CharacterObject.CharacterName + strSpace + '-' + strSpace + FormMode + strSpace + '(' + CharacterObjectSettings.Name + ')';
            if (_blnIsDirty)
                strTitle += '*';
            this.DoThreadSafe(x => x.Text = strTitle);
        }

        /// <summary>
        /// Update the Window title to show the Character's name and unsaved changes status.
        /// </summary>
        protected async Task UpdateWindowTitleAsync(bool blnCanSkip, CancellationToken token = default)
        {
            CancellationTokenSource objSource = null;
            if (token != GenericToken)
            {
                objSource = CancellationTokenSource.CreateLinkedTokenSource(token, GenericToken);
                token = objSource.Token;
            }

            try
            {
                token.ThrowIfCancellationRequested();
                if (Text.EndsWith('*') == _blnIsDirty && blnCanSkip)
                    return;
                string strSpace = await LanguageManager.GetStringAsync("String_Space", token: token);
                string strTitle = CharacterObject.CharacterName + strSpace + '-' + strSpace + FormMode + strSpace + '('
                                  + CharacterObjectSettings.Name + ')';
                if (_blnIsDirty)
                    strTitle += '*';
                await this.DoThreadSafeAsync(x => x.Text = strTitle, token);
            }
            finally
            {
                objSource?.Dispose();
            }
        }

        /// <summary>
        /// Save the Character.
        /// </summary>
        public virtual async ValueTask<bool> SaveCharacter(bool blnNeedConfirm = true, bool blnDoCreated = false, CancellationToken token = default)
        {
            CancellationTokenSource objSource = null;
            if (token != GenericToken)
            {
                objSource = CancellationTokenSource.CreateLinkedTokenSource(token, GenericToken);
                token = objSource.Token;
            }

            try
            {
                CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
                try
                {
                    // If the Character does not have a file name, trigger the Save As menu item instead.
                    if (string.IsNullOrEmpty(CharacterObject.FileName))
                    {
                        return await SaveCharacterAs(blnDoCreated, token);
                    }

                    if (blnDoCreated)
                    {
                        // If the Created is checked, make sure the user wants to actually save this character.
                        if (blnNeedConfirm && !await ConfirmSaveCreatedCharacter(token))
                            return false;
                        // If this character has just been saved as Created, close this form and re-open the character which will open it in the Career window instead.
                        return await SaveCharacterAsCreated(token);
                    }

                    using (ThreadSafeForm<LoadingBar> frmLoadingBar
                           = await Program.CreateAndShowProgressBarAsync(token: token))
                    {
                        await frmLoadingBar.MyForm.PerformStepAsync(CharacterObject.CharacterName,
                                                                    LoadingBar.ProgressBarTextPatterns.Saving, token);
                        if (_objCharacterFileWatcher != null)
                            _objCharacterFileWatcher.Changed -= LiveUpdateFromCharacterFile;
                        try
                        {
                            if (!await CharacterObject.SaveAsync(token: token))
                                return false;
                        }
                        finally
                        {
                            if (_objCharacterFileWatcher != null)
                                _objCharacterFileWatcher.Changed += LiveUpdateFromCharacterFile;
                        }

                        await GlobalSettings.MostRecentlyUsedCharacters.InsertAsync(0, CharacterObject.FileName, token);
                        await SetDirty(false, token);
                    }

                    return true;
                }
                finally
                {
                    await objCursorWait.DisposeAsync();
                }
            }
            finally
            {
                objSource?.Dispose();
            }
        }

        /// <summary>
        /// Save the Character using the Save As dialogue box.
        /// </summary>
        public virtual async ValueTask<bool> SaveCharacterAs(bool blnDoCreated = false, CancellationToken token = default)
        {
            CancellationTokenSource objSource = null;
            if (token != GenericToken)
            {
                objSource = CancellationTokenSource.CreateLinkedTokenSource(token, GenericToken);
                token = objSource.Token;
            }

            try
            {
                CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
                try
                {
                    // If the Created is checked, make sure the user wants to actually save this character.
                    if (blnDoCreated && !await ConfirmSaveCreatedCharacter(token))
                    {
                        return false;
                    }

                    string strOldFileName = CharacterObject.FileName;
                    string strShowFileName = Path.GetFileName(CharacterObject.FileName);
                    if (string.IsNullOrEmpty(strShowFileName))
                    {
                        strShowFileName = CharacterObject.CharacterName.CleanForFileName();
                    }

                    dlgSaveFile.FileName = strShowFileName;
                    if (await this.DoThreadSafeFuncAsync(x => dlgSaveFile.ShowDialog(x), token: token)
                        != DialogResult.OK)
                        return false;

                    string strFileName = dlgSaveFile.FileName;
                    if (!string.IsNullOrEmpty(strFileName)
                        && !strFileName.EndsWith(".chum5", StringComparison.OrdinalIgnoreCase)
                        && !strFileName.EndsWith(".chum5lz", StringComparison.OrdinalIgnoreCase))
                    {
                        strFileName += strShowFileName.EndsWith(".chum5lz", StringComparison.OrdinalIgnoreCase)
                            ? ".chum5lz"
                            : ".chum5";
                    }
                    CharacterObject.FileName = strFileName;
                    try
                    {
                        bool blnReturn = await SaveCharacter(false, blnDoCreated, token);
                        if (!blnReturn)
                            CharacterObject.FileName = strOldFileName;
                        return blnReturn;
                    }
                    catch
                    {
                        CharacterObject.FileName = strOldFileName;
                        throw;
                    }
                }
                finally
                {
                    await objCursorWait.DisposeAsync();
                }
            }
            finally
            {
                objSource?.Dispose();
            }
        }

        /// <summary>
        /// Save the character as Created and re-open it in Career Mode.
        /// </summary>
        public virtual Task<bool> SaveCharacterAsCreated(CancellationToken token = default) { return Task.FromResult(false); }

        /// <summary>
        /// Verify that the user wants to save this character as Created.
        /// </summary>
        public virtual Task<bool> ConfirmSaveCreatedCharacter(CancellationToken token = default) { return Task.FromResult(true); }

        public async Task DoPrint(CancellationToken token = default)
        {
            if (!await Program.SwitchToOpenPrintCharacter(CharacterObject, token))
                await Program.OpenCharacterForPrinting(CharacterObject, token: token);
        }

        public async Task DoExport(CancellationToken token = default)
        {
            if (!await Program.SwitchToOpenExportCharacter(CharacterObject, token))
                await Program.OpenCharacterForExport(CharacterObject, token: token);
        }

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _objCharacter.PropertyChanged -= CharacterPropertyChanged;
                Interlocked.Exchange(ref _objCharacterFileWatcher, null)?.Dispose();
                CancellationTokenSource objTemp = Interlocked.Exchange(ref _objUpdateCharacterInfoCancellationTokenSource, null);
                if (objTemp != null)
                {
                    try
                    {
                        objTemp.Cancel(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        //swallow this
                    }
                    finally
                    {
                        objTemp.Dispose();
                    }
                }
                _objUpdateCharacterInfoSemaphoreSlim.Dispose();
                dlgSaveFile.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Vehicles Tab

        public async ValueTask PurchaseVehicleGear(Vehicle objSelectedVehicle, Location objLocation = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            CursorWait objCursorWait = await CursorWait.NewAsync(this, token: token);
            try
            {
                XmlDocument objXmlDocument = await CharacterObject.LoadDataAsync("gear.xml", token: token);
                bool blnAddAgain;

                do
                {
                    using (ThreadSafeForm<SelectGear> frmPickGear
                           = await ThreadSafeForm<SelectGear>.GetAsync(
                               () => new SelectGear(CharacterObject, 0, 1, objSelectedVehicle), token))
                    {
                        if (await frmPickGear.ShowDialogSafeAsync(this, token) == DialogResult.Cancel)
                            break;
                        blnAddAgain = frmPickGear.MyForm.AddAgain;

                        // Open the Gear XML file and locate the selected piece.
                        XmlNode objXmlGear = objXmlDocument.SelectSingleNode("/chummer/gears/gear[id = " +
                                                                             frmPickGear.MyForm.SelectedGear
                                                                                 .CleanXPath()
                                                                             + ']');

                        // Create the new piece of Gear.
                        List<Weapon> lstWeapons = new List<Weapon>(1);

                        Gear objGear = new Gear(CharacterObject);
                        objGear.Create(objXmlGear, frmPickGear.MyForm.SelectedRating, lstWeapons, string.Empty, false);

                        if (objGear.InternalId.IsEmptyGuid())
                            continue;

                        objGear.Quantity = frmPickGear.MyForm.SelectedQty;
                        objGear.DiscountCost = frmPickGear.MyForm.BlackMarketDiscount;

                        // Reduce the cost for Do It Yourself components.
                        if (frmPickGear.MyForm.DoItYourself)
                            objGear.Cost = '(' + objGear.Cost + ") * 0.5";
                        // If the item was marked as free, change its cost.
                        if (frmPickGear.MyForm.FreeCost)
                            objGear.Cost = "0";

                        if (CharacterObject.Created)
                        {
                            decimal decCost = objGear.TotalCost;

                            // Multiply the cost if applicable.
                            char chrAvail = (await objGear.TotalAvailTupleAsync(token: token)).Suffix;
                            switch (chrAvail)
                            {
                                case 'R' when CharacterObjectSettings.MultiplyRestrictedCost:
                                    decCost *= CharacterObjectSettings.RestrictedCostMultiplier;
                                    break;

                                case 'F' when CharacterObjectSettings.MultiplyForbiddenCost:
                                    decCost *= CharacterObjectSettings.ForbiddenCostMultiplier;
                                    break;
                            }

                            // Check the item's Cost and make sure the character can afford it.
                            if (!frmPickGear.MyForm.FreeCost)
                            {
                                if (decCost > CharacterObject.Nuyen)
                                {
                                    Program.ShowMessageBox(this,
                                                           await LanguageManager.GetStringAsync(
                                                               "Message_NotEnoughNuyen", token: token),
                                                           await LanguageManager.GetStringAsync(
                                                               "MessageTitle_NotEnoughNuyen", token: token),
                                                           MessageBoxButtons.OK,
                                                           MessageBoxIcon.Information);
                                    continue;
                                }

                                // Create the Expense Log Entry.
                                ExpenseLogEntry objExpense = new ExpenseLogEntry(CharacterObject);
                                objExpense.Create(decCost * -1,
                                                  await LanguageManager.GetStringAsync(
                                                      "String_ExpensePurchaseVehicleGear", token: token) +
                                                  await LanguageManager.GetStringAsync("String_Space", token: token) +
                                                  objGear.CurrentDisplayNameShort, ExpenseType.Nuyen,
                                                  DateTime.Now);
                                await CharacterObject.ExpenseEntries.AddWithSortAsync(objExpense, token: token);
                                CharacterObject.Nuyen -= decCost;

                                ExpenseUndo objUndo = new ExpenseUndo();
                                objUndo.CreateNuyen(NuyenExpenseType.AddVehicleGear, objGear.InternalId, 1);
                                objExpense.Undo = objUndo;
                            }
                        }

                        Gear objExistingGear = null;
                        // If this is Ammunition, see if the character already has it on them.
                        if ((objGear.Category == "Ammunition" ||
                             !string.IsNullOrEmpty(objGear.AmmoForWeaponType)) && frmPickGear.MyForm.Stack)
                        {
                            objExistingGear =
                                objSelectedVehicle.GearChildren.FirstOrDefault(x =>
                                                                                   objGear.IsIdenticalToOtherGear(x));
                        }

                        if (objExistingGear != null)
                        {
                            // A match was found, so increase the quantity instead.
                            objExistingGear.Quantity += objGear.Quantity;
                        }
                        else
                        {
                            // Add the Gear to the Vehicle.
                            if (objLocation != null)
                                await objLocation.Children.AddAsync(objGear, token);
                            await objSelectedVehicle.GearChildren.AddAsync(objGear, token);
                            objGear.Parent = objSelectedVehicle;

                            foreach (Weapon objWeapon in lstWeapons)
                            {
                                if (objLocation != null)
                                    await objLocation.Children.AddAsync(objGear, token);
                                objWeapon.ParentVehicle = objSelectedVehicle;
                                await objSelectedVehicle.Weapons.AddAsync(objWeapon, token);
                            }
                        }
                    }

                    await RequestCharacterUpdate(token);

                    await SetDirty(true, token);
                } while (blnAddAgain);
            }
            finally
            {
                await objCursorWait.DisposeAsync();
            }
        }

        #endregion Vehicles Tab
    }
}
