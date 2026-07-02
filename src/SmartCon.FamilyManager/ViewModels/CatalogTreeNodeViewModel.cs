using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartCon.FamilyManager.ViewModels;

public abstract partial class CatalogTreeNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Родительский узел в дереве. <c>null</c> для корневых нодов (TreeNodes / RootNodes).
    /// Заполняется автоматически при добавлении узла в <see cref="Children"/> родителя
    /// (см. <see cref="OnChildCollectionChanged"/>).
    /// Используется StickyCategoryHeaderBehavior для построения стека прилипающих заголовков
    /// и будущими фичами (поиск категории по ID, экспорт, и т.д.).
    /// </summary>
    public CatalogTreeNodeViewModel? Parent { get; internal set; }

    /// <summary>
    /// Дочерние узлы. Кастомный setter (вместо [ObservableProperty]) — подписывается на
    /// <see cref="ObservableCollection{T}.CollectionChanged"/> и автоматически
    /// проставляет <see cref="Parent"/> при добавлении и снимает при удалении.
    /// При первой инициализации через field initializer (<c>[]</c>) коллекция создаётся
    /// в lazy-getter и подписка ставится немедленно.
    /// </summary>
    private ObservableCollection<CatalogTreeNodeViewModel>? _children;

    public ObservableCollection<CatalogTreeNodeViewModel> Children
    {
        get
        {
            if (_children is null)
            {
                _children = [];
                _children.CollectionChanged += OnChildCollectionChanged;
            }
            return _children;
        }
        set
        {
            if (_children is not null)
                _children.CollectionChanged -= OnChildCollectionChanged;

            _children = value ?? [];
            _children.CollectionChanged += OnChildCollectionChanged;

            foreach (var child in _children)
                child.Parent = this;

            OnPropertyChanged();
        }
    }

    private void OnChildCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (CatalogTreeNodeViewModel item in e.NewItems)
                item.Parent = this;
        }

        if (e.OldItems is not null)
        {
            foreach (CatalogTreeNodeViewModel item in e.OldItems)
            {
                if (item.Parent == this)
                    item.Parent = null;
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Reset не даёт OldItems — обнуляем Parent для всех, кто указывал на нас.
            // Безопасно: если Parent уже другой, не трогаем.
            foreach (var child in _children!)
            {
                if (child.Parent == this)
                    child.Parent = null;
            }
        }
    }

    public abstract bool IsCategory { get; }
    public virtual bool IsType => false;

    /// <summary>
    /// Re-raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for the
    /// <see cref="IsExpanded"/> property without changing the underlying value.
    /// Used by <see cref="FamilyManagerMainViewModel.LoadTreeAsync"/> after the
    /// <c>TreeNodes</c> collection swap to compensate for a known WPF TreeView
    /// container-generation quirk where a TwoWay IsExpanded binding fails to
    /// pick up the VM source value on the freshly-created container. This is a
    /// no-op on platforms where the binding already worked — it just emits an
    /// extra notification which the View either ignores (no subscribers) or
    /// uses to re-sync the TreeViewItem.IsExpanded visual state.
    /// </summary>
    internal void NotifyIsExpandedChanged()
    {
        OnPropertyChanged(nameof(IsExpanded));
    }
}