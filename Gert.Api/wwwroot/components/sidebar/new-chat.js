// components/sidebar/new-chat.js — start a new conversation.
import van from "van";
import { Icon } from "../../icons/icons.js";
import * as chat from "../../state/chat.js";
import * as artifacts from "../../state/artifacts.js";
import { navigate } from "../../lib/router.js";

const { button } = van.tags;

export const NewChat = () =>
  button(
    {
      class: "newchat",
      onclick: () => {
        chat.newConversation();
        artifacts.clear();
        navigate("/");
      },
    },
    Icon("plus", { size: 15, strokeWidth: 2.2 }),
    "New chat",
  );
