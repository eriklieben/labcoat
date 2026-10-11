// Icon set: Fluent UI System Icons (MIT) imported as raw SVG so only the icons we use are bundled.
import play from "@fluentui/svg-icons/icons/play_20_regular.svg?raw";
import playFilled from "@fluentui/svg-icons/icons/play_20_filled.svg?raw";
import stop from "@fluentui/svg-icons/icons/stop_20_regular.svg?raw";
import stopFilled from "@fluentui/svg-icons/icons/stop_20_filled.svg?raw";
import eye from "@fluentui/svg-icons/icons/eye_20_regular.svg?raw";
import eyeFilled from "@fluentui/svg-icons/icons/eye_20_filled.svg?raw";
import arrowSync from "@fluentui/svg-icons/icons/arrow_sync_20_regular.svg?raw";
import arrowClockwise from "@fluentui/svg-icons/icons/arrow_clockwise_20_regular.svg?raw";
import checkCircle from "@fluentui/svg-icons/icons/checkmark_circle_20_filled.svg?raw";
import dismissCircle from "@fluentui/svg-icons/icons/dismiss_circle_20_filled.svg?raw";
import warning from "@fluentui/svg-icons/icons/warning_20_filled.svg?raw";
import errorCircle from "@fluentui/svg-icons/icons/error_circle_20_filled.svg?raw";
import circle from "@fluentui/svg-icons/icons/circle_20_regular.svg?raw";
import subtractCircle from "@fluentui/svg-icons/icons/subtract_circle_20_regular.svg?raw";
import search from "@fluentui/svg-icons/icons/search_20_regular.svg?raw";
import chevronRight from "@fluentui/svg-icons/icons/chevron_right_20_regular.svg?raw";
import chevronDown from "@fluentui/svg-icons/icons/chevron_down_20_regular.svg?raw";
import chevronUp from "@fluentui/svg-icons/icons/chevron_up_20_regular.svg?raw";
import info from "@fluentui/svg-icons/icons/info_20_regular.svg?raw";
import open from "@fluentui/svg-icons/icons/open_20_regular.svg?raw";
import code from "@fluentui/svg-icons/icons/code_20_regular.svg?raw";
import moon from "@fluentui/svg-icons/icons/weather_moon_20_regular.svg?raw";
import sun from "@fluentui/svg-icons/icons/weather_sunny_20_regular.svg?raw";
import darkTheme from "@fluentui/svg-icons/icons/dark_theme_20_regular.svg?raw";
import collapseAll from "@fluentui/svg-icons/icons/text_collapse_20_regular.svg?raw";
import expandAll from "@fluentui/svg-icons/icons/text_expand_20_regular.svg?raw";
import copy from "@fluentui/svg-icons/icons/copy_20_regular.svg?raw";
import dismiss from "@fluentui/svg-icons/icons/dismiss_20_regular.svg?raw";
import plugDisconnected from "@fluentui/svg-icons/icons/plug_disconnected_20_regular.svg?raw";
import timer from "@fluentui/svg-icons/icons/timer_20_regular.svg?raw";
import beaker from "@fluentui/svg-icons/icons/beaker_20_regular.svg?raw";
import beakerFilled from "@fluentui/svg-icons/icons/beaker_24_filled.svg?raw";
// Labcoat logo: a lab coat whose open collar forms a check mark (own artwork, MIT with the project).
import labcoat from "../assets/labcoat.svg?raw";
import labcoatSmall from "../assets/labcoat-16.svg?raw"; // optical variant for 16–20 px
import console_ from "@fluentui/svg-icons/icons/window_console_20_regular.svg?raw";
import taskList from "@fluentui/svg-icons/icons/task_list_square_ltr_20_regular.svg?raw";
import textWrap from "@fluentui/svg-icons/icons/text_wrap_20_regular.svg?raw";
import del from "@fluentui/svg-icons/icons/delete_20_regular.svg?raw";
import history from "@fluentui/svg-icons/icons/history_20_regular.svg?raw";
import shieldCheck from "@fluentui/svg-icons/icons/shield_checkmark_20_regular.svg?raw";
import shieldCheckFilled from "@fluentui/svg-icons/icons/shield_checkmark_20_filled.svg?raw";
import arrowLeft from "@fluentui/svg-icons/icons/arrow_left_20_regular.svg?raw";
import dataPie from "@fluentui/svg-icons/icons/data_pie_20_regular.svg?raw";
import sortUp from "@fluentui/svg-icons/icons/arrow_sort_up_20_regular.svg?raw";
import sortDown from "@fluentui/svg-icons/icons/arrow_sort_down_20_regular.svg?raw";
import sort from "@fluentui/svg-icons/icons/arrow_sort_20_regular.svg?raw";
import coverageView from "@fluentui/svg-icons/icons/document_percent_20_regular.svg?raw";
import documentIcon from "@fluentui/svg-icons/icons/document_20_regular.svg?raw";
import arrowReset from "@fluentui/svg-icons/icons/arrow_reset_20_regular.svg?raw";
import fire from "@fluentui/svg-icons/icons/fire_20_filled.svg?raw";
import targetArrow from "@fluentui/svg-icons/icons/target_arrow_20_regular.svg?raw";
import settings from "@fluentui/svg-icons/icons/settings_20_regular.svg?raw";
import checkmark from "@fluentui/svg-icons/icons/checkmark_20_regular.svg?raw";
import bug from "@fluentui/svg-icons/icons/bug_20_regular.svg?raw";
import groupList from "@fluentui/svg-icons/icons/group_list_20_regular.svg?raw";
import link from "@fluentui/svg-icons/icons/link_20_regular.svg?raw";
import arrowRepeat from "@fluentui/svg-icons/icons/arrow_repeat_all_20_regular.svg?raw";
import shieldProhibited from "@fluentui/svg-icons/icons/shield_prohibited_20_regular.svg?raw";
import branch from "@fluentui/svg-icons/icons/branch_20_regular.svg?raw";
import arrowDownload from "@fluentui/svg-icons/icons/arrow_download_20_regular.svg?raw";

export const ICONS = {
  play,
  playFilled,
  stop,
  stopFilled,
  eye,
  eyeFilled,
  arrowSync,
  arrowClockwise,
  checkCircle,
  dismissCircle,
  warning,
  errorCircle,
  circle,
  subtractCircle,
  search,
  chevronRight,
  chevronDown,
  chevronUp,
  open,
  info,
  code,
  moon,
  sun,
  darkTheme,
  collapseAll,
  expandAll,
  copy,
  dismiss,
  plugDisconnected,
  timer,
  beaker,
  beakerFilled,
  labcoat,
  labcoatSmall,
  console: console_,
  taskList,
  textWrap,
  del,
  history,
  arrowReset,
  shieldCheck,
  shieldCheckFilled,
  arrowLeft,
  dataPie,
  sortUp,
  sortDown,
  /** plain sort (arrow_sort): the tree's sort menu button */
  sort,
  /** a source file with a percentage: the Coverage view tab (the rail's report page uses dataPie) */
  coverageView,
  document: documentIcon,
  fire,
  targetArrow,
  settings,
  checkmark,
  /** Debug test (attach a debugger) */
  bug,
  /** the tree's group-by menu */
  groupList,
  link,
  /** Run N× (repeat) */
  arrowRepeat,
  /** quarantine */
  shieldProhibited,
  /** git branch: Run affected (changes vs a base) */
  branch,
  /** export / download (JUnit, HTML report, lcov) */
  arrowDownload,
};
