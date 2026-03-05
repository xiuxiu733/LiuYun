using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace LiuYun.Views
{
    public sealed class EmojiCategory
    {
        public string Name { get; }
        public ObservableCollection<string> Items { get; }

        public EmojiCategory(string name, IEnumerable<string> items)
        {
            Name = name;
            Items = new ObservableCollection<string>(items);
        }
    }

    public sealed partial class EmojiPage : Page
    {
        private const int ColumnCount = 3;
        private const double ItemMargin = 3;

        public ObservableCollection<EmojiCategory> Categories { get; } = new ObservableCollection<EmojiCategory>
        {
            new EmojiCategory("开心", new[]
            {
                "(＾▽＾)", "(≧▽≦)", "(ﾉ◕ヮ◕)ﾉ*:･ﾟ✧", "(๑˃̵ᴗ˂̵)و", "(*^▽^*)", "(•̀ᴗ•́)و ̑̑",
                "(￣▽￣)~*", "(✧ω✧)", "(灬º‿º灬)♡", "ヽ(•‿•)ノ", "(づ｡◕‿‿◕｡)づ", "٩(◕‿◕)۶"
            }),
            new EmojiCategory("问候", new[]
            {
                "(´｡• ᵕ •｡`)", "(＾-＾)ノ", "ヾ(•ω•`)o", "(｡･ω･)ﾉﾞ", "(*´∀`)ﾉ", "(^_^)／",
                "ヽ(°〇°)ﾉ", "(*￣▽￣)b", "(￣ω￣)/", "( ´ ▽ ` )ﾉ", "(=ﾟωﾟ)ﾉ", "(•‿•)／"
            }),
            new EmojiCategory("卖萌", new[]
            {
                "(๑•́ ₃ •̀๑)", "(=^･ω･^=)", "(ฅ'ω'ฅ)", "(●'◡'●)", "(｡♥‿♥｡)", "(っ˘ω˘ς )",
                "(*/ω＼*)", "(≖‿≖)✧", "(｡•̀ᴗ-)✧", "(>ω<)", "(づ￣ ³￣)づ", "٩(｡•ㅅ•｡)و"
            }),
            new EmojiCategory("忧伤", new[]
            {
                "(T_T)", "(ಥ﹏ಥ)", "(；＿；)", "(｡•́︿•̀｡)", "(ノ_<。)", "(｡╯︵╰｡)",
                "(╥﹏╥)", "(´；ω；`)", "(つ﹏<)･ﾟ｡", "(；ω；)", "(＞﹏＜)", "(っ˘̩╭╮˘̩)っ"
            }),
            new EmojiCategory("生气", new[]
            {
                "(╬▔皿▔)╯", "(#`Д´)", "(＃`O´)", "(▼皿▼#)", "(｀へ´)", "(눈_눈)",
                "(ง'̀-'́)ง", "(╯°□°）╯︵ ┻━┻", "(￣^￣)", "(•`_´•)", "ヽ(`⌒´メ)ノ", "(ꐦ°᷄д°᷅)"
            }),
            new EmojiCategory("无语", new[]
            {
                "(¬_¬)", "(¬¬)", "( -_-)", "(._.)", "(ー_ー)!!", "(￣ー￣)",
                "(￣△￣；)", "(・・;)", "(；一_一)", "(；¬д¬)", "(=ω=)", "(￢_￢)"
            }),
            new EmojiCategory("惊讶", new[]
            {
                "(°ロ°) !", "(⊙_⊙)", "(ﾟдﾟ)", "(O_O)", "Σ(っ °Д °;)っ", "(⊙ω⊙)",
                "(゜-゜)", "(°ー°〃)", "(ﾟ〇ﾟ)", "∑(O_O;)", "(☉｡☉)!", "(ﾟロﾟ;)"
            }),
            new EmojiCategory("经典 Ascii 表情符号", new[]
            {
                ":)", ":D", ";)", ":P", ":|", ":(",
                "T_T", "xD", "^_^", "-_-", "o_O", "O_O",
                "(>_<)", "(///_///)", "q(≧▽≦q)", "\\(^o^)/", "^_^\"", "(\"•͈ᴗ•͈\")"
            })
        };

        public EmojiPage()
        {
            InitializeComponent();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame?.CanGoBack == true)
            {
                Frame.GoBack();
                return;
            }

            Frame?.Navigate(typeof(ClipboardPage));
        }

        private void KaomojiGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is string value)
            {
                CopyText(value);
            }
        }

        private void CategoryGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is GridView gridView && gridView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            {
                double availableWidth = gridView.ActualWidth;
                if (availableWidth <= 0) return;

                double totalMargin = ItemMargin * 2 * ColumnCount;
                double itemWidth = (availableWidth - totalMargin) / ColumnCount;
                if (itemWidth > 0)
                {
                    wrapGrid.ItemWidth = itemWidth;
                }
            }
        }

        private static void CopyText(string value)
        {
            DataPackage dataPackage = new DataPackage();
            dataPackage.SetText(value);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
        }
    }
}
